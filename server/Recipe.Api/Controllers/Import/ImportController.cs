using System.Security.Claims;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Recipe.Api.Common;
using Recipe.Api.Common.Exceptions;
using Recipe.Api.Dtos.Import;
using Recipe.Api.Services.Import;
using Recipe.Api.Services.Metadata;

namespace Recipe.Api.Controllers.Import;

/// <summary>
/// Intake for saved posts parsed out of a platform export on device. The app unzips the
/// archive locally and posts a normalised array — roughly 60KB instead of a 200MB upload,
/// and the user's archive never reaches this server.
/// </summary>
[ApiController]
[Authorize]
[ApiVersion("1.0")]
[Route("api/[controller]")]
[Route("api/v{version:apiVersion}/[controller]")]
public class ImportController(IImportService importService, IMetadataService metadataService) : ControllerBase
{
    /// <summary>
    /// Upload ceiling. The largest real export seen is under 1MB, so this is generous;
    /// it exists so a single request cannot become unbounded work.
    /// </summary>
    private const long MaxUploadBytes = 100_000_000;

    /// <summary>Submits a batch of saved posts. Items already imported by this user are skipped.</summary>
    /// <response code="201">The batch was stored. The body reports how many were new.</response>
    /// <response code="400">The payload failed validation.</response>
    [HttpPost]
    [ProducesResponseType(typeof(ImportSummaryDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create(
        [FromBody] CreateImportRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (userId is null)
        {
            return Unauthorized();
        }

        try
        {
            var summary = await importService.CreateAsync(userId, request, cancellationToken);
            return CreatedAtAction(nameof(Get), new { id = summary.Id }, summary);
        }
        catch (DomainValidationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Uploads a raw platform export file and imports what it contains.
    /// </summary>
    /// <remarks>
    /// The shipping app parses the export on device and posts to <c>POST /api/import</c>
    /// instead — a 60KB array rather than a 200MB archive, and the user's export never
    /// reaches this server. This endpoint is the documented fallback for exports the
    /// on-device parser cannot read, and the way to test against a real file.
    ///
    /// Accepts Instagram's <c>saved_posts.json</c> or TikTok's
    /// <c>user_data_tiktok.json</c>; the platform is detected from the file.
    /// </remarks>
    /// <param name="file">The export JSON. Not a zip — extract it first.</param>
    /// <param name="includeLikes">
    /// TikTok only, default false. Likes are ambient scrolling: eight times the volume of
    /// favourites, capped by the export, and mostly not recipes.
    /// </param>
    /// <param name="cancellationToken"></param>
    /// <response code="201">Parsed and imported. The body reports how many were new and how many records were unusable.</response>
    /// <response code="400">The file was missing, too large, not JSON, or not a recognised export.</response>
    [HttpPost("file")]
    [RequestSizeLimit(MaxUploadBytes)]
    [ProducesResponseType(typeof(ImportSummaryDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateFromFile(
        IFormFile file,
        CancellationToken cancellationToken,
        [FromQuery] bool includeLikes = false)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (userId is null)
        {
            return Unauthorized();
        }

        if (file is null || file.Length == 0)
        {
            return BadRequest("Attach an export JSON file.");
        }

        if (file.Length > MaxUploadBytes)
        {
            return BadRequest($"That file is {file.Length / 1_000_000}MB; the limit is {MaxUploadBytes / 1_000_000}MB.");
        }

        try
        {
            await using var stream = file.OpenReadStream();
            var parsed = ExportParser.Parse(stream, includeLikes);

            if (parsed.Posts.Count == 0)
            {
                return BadRequest(
                    $"No usable posts found in that file ({parsed.SkippedCount} records skipped). " +
                    "For TikTok, check the export contains favourites.");
            }

            var summary = await importService.CreateAsync(
                userId,
                new CreateImportRequest { Platform = parsed.Platform, Posts = parsed.Posts },
                cancellationToken);

            return CreatedAtAction(
                nameof(Get),
                new { id = summary.Id },
                summary with { SkippedCount = parsed.SkippedCount });
        }
        catch (DomainValidationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>Lists this user's imports, newest first.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PaginatedResult<ImportSummaryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        CancellationToken cancellationToken,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 20)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (userId is null)
        {
            return Unauthorized();
        }

        return Ok(await importService.ListAsync(userId, pageNumber, pageSize, cancellationToken));
    }

    /// <summary>Returns one import's summary.</summary>
    /// <response code="404">No such import for this user.</response>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ImportSummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (userId is null)
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await importService.GetAsync(userId, id, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
    }

    /// <summary>
    /// Runs stage 1 for pending posts in this import: fetches caption, creator, and
    /// thumbnail from TikTok's public oEmbed endpoint.
    /// </summary>
    /// <remarks>
    /// TikTok exports carry nothing but a date and a link, so nothing downstream —
    /// classification, extraction, creator clustering — can run until this has. Instagram
    /// posts skip it; their export already carries captions.
    ///
    /// Synchronous and capped, roughly a third of a second per post. A full backlog is
    /// queue work; this exists so the pipeline is usable before the queue lands. Call it
    /// repeatedly until <c>remaining</c> reaches zero, and back off when
    /// <c>stoppedEarly</c> comes back true.
    /// </remarks>
    /// <response code="200">The run finished. Counts describe what happened.</response>
    /// <response code="404">No such import for this user.</response>
    [HttpPost("{id:guid}/metadata")]
    [ProducesResponseType(typeof(MetadataRunDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> FetchMetadata(
        Guid id,
        CancellationToken cancellationToken,
        [FromQuery] int limit = 50)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (userId is null)
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await metadataService.FetchPendingAsync(userId, id, limit, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
    }

    /// <summary>Runs stage 1 for a single saved post.</summary>
    /// <response code="404">No such saved post for this user.</response>
    [HttpPost("posts/{savedPostId:guid}/metadata")]
    [ProducesResponseType(typeof(SavedPostDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> FetchPostMetadata(Guid savedPostId, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (userId is null)
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await metadataService.FetchAsync(userId, savedPostId, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
    }

    /// <summary>
    /// The review pile — posts classification could not call either way.
    /// </summary>
    /// <remarks>
    /// Ranked most-likely-food first, so the easy yeses come before the genuinely marginal
    /// ones. Thumbnails are included; a review pile without them is unusable.
    /// </remarks>
    [HttpGet("review")]
    [ProducesResponseType(typeof(PaginatedResult<SavedPostDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Review(
        CancellationToken cancellationToken,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 50)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        return userId is null
            ? Unauthorized()
            : Ok(await importService.ListForReviewAsync(userId, pageNumber, pageSize, cancellationToken));
    }

    /// <summary>Settles a batch of review decisions.</summary>
    /// <remarks>
    /// Approved posts are queued for extraction immediately. Rejected ones become
    /// "skipped" and stay visible — that list is the safety valve that makes it safe to
    /// tune classification for precision in the first place.
    /// </remarks>
    [HttpPost("review")]
    [ProducesResponseType(typeof(ReviewResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Review(
        [FromBody] ReviewDecisionRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        return userId is null
            ? Unauthorized()
            : Ok(await importService.ReviewAsync(userId, request, cancellationToken));
    }

    /// <summary>Lists the posts stored by one import.</summary>
    /// <response code="404">No such import for this user.</response>
    [HttpGet("{id:guid}/posts")]
    [ProducesResponseType(typeof(PaginatedResult<SavedPostDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListPosts(
        Guid id,
        CancellationToken cancellationToken,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 20)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (userId is null)
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await importService.ListPostsAsync(userId, id, pageNumber, pageSize, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
    }
}
