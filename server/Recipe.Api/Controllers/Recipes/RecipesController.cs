using System.Security.Claims;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Recipe.Api.Common;
using Recipe.Api.Common.Exceptions;
using Recipe.Api.Dtos.Recipes;
using Recipe.Api.Models.Recipes;
using Recipe.Api.Services.Recipes;

namespace Recipe.Api.Controllers.Recipes;

/// <summary>
/// Structured recipes extracted from saved posts. Extraction runs through the Python
/// sidecar, which fetches the media transiently and never persists it.
/// </summary>
[ApiController]
[Authorize]
[ApiVersion("1.0")]
[Route("api/[controller]")]
[Route("api/v{version:apiVersion}/[controller]")]
public class RecipesController(IRecipeService recipeService) : ControllerBase
{
    /// <summary>Turns a single shared link into a recipe.</summary>
    /// <remarks>
    /// The share-sheet path, and the app's primary way in. Accepts any TikTok or Instagram
    /// link shape, including the <c>vm.tiktok.com</c> short links the share sheet produces.
    ///
    /// The post is added to the user's cookbook either way. When anyone has already
    /// extracted the same video, the stored result is served immediately — no fetch, no
    /// transcription, no model call.
    /// </remarks>
    /// <response code="200">Already extracted — the shared cache had it. Ready to read.</response>
    /// <response code="202">
    /// Queued. The body carries the recipe row with <c>status: "Processing"</c>; poll
    /// <c>GET /api/recipes/{id}</c> until it settles.
    /// </response>
    /// <response code="400">The link could not be read as a TikTok or Instagram post.</response>
    [HttpPost("from-url")]
    [ProducesResponseType(typeof(RecipeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(RecipeDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> FromUrl(
        [FromBody] ExtractFromUrlRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (userId is null)
        {
            return Unauthorized();
        }

        try
        {
            var recipe = await recipeService.ExtractFromUrlAsync(userId, request.Url, cancellationToken);

            return recipe.Status == ExtractionStatus.Processing
                ? Accepted($"/api/recipes/{recipe.Id}", recipe)
                : Ok(recipe);
        }
        catch (DomainValidationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>Extracts a recipe from one saved post, or re-runs an earlier attempt.</summary>
    /// <remarks>
    /// Synchronous and slow — transcription runs at roughly a fifth of realtime. Fine for
    /// one post; a whole backlog belongs on the queue.
    ///
    /// A video that narrates too little comes back as <c>NeedsVision</c> rather than an
    /// error: the method is on-screen text and belongs on the vision path.
    /// </remarks>
    /// <response code="200">Extraction ran. Check <c>status</c> for the outcome.</response>
    /// <response code="400">The post cannot be located on its platform yet.</response>
    /// <response code="404">No such saved post for this user.</response>
    [HttpPost("extract/{savedPostId:guid}")]
    [ProducesResponseType(typeof(RecipeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Extract(Guid savedPostId, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (userId is null)
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await recipeService.ExtractAsync(userId, savedPostId, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (DomainValidationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>Lists and searches this user's recipes, most confident first.</summary>
    /// <param name="cancellationToken"></param>
    /// <param name="q">
    /// Free-text search across title, ingredients, equipment, and creator. Stemmed and
    /// ranked on Postgres; substring matching on SQLite, which the test suite uses.
    /// </param>
    /// <param name="status">Optional filter, e.g. <c>NeedsVision</c> to find the vision backlog.</param>
    /// <param name="pageNumber"></param>
    /// <param name="pageSize"></param>
    [HttpGet]
    [ProducesResponseType(typeof(PaginatedResult<RecipeSummaryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        CancellationToken cancellationToken,
        [FromQuery] string? q = null,
        [FromQuery] ExtractionStatus? status = null,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 20)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (userId is null)
        {
            return Unauthorized();
        }

        return Ok(await recipeService.ListAsync(userId, status, q, pageNumber, pageSize, cancellationToken));
    }

    /// <summary>Replaces the user-editable fields of a recipe.</summary>
    /// <remarks>
    /// Send the whole recipe, not a delta. Edited recipes are flagged, and every ingredient
    /// the user typed is recorded at full confidence.
    /// </remarks>
    /// <response code="200">The updated recipe.</response>
    /// <response code="400">The payload failed validation.</response>
    /// <response code="404">No such recipe for this user.</response>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(RecipeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        Guid id,
        [FromBody] UpdateRecipeRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (userId is null)
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await recipeService.UpdateAsync(userId, id, request, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (DomainValidationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>Returns one recipe in full.</summary>
    /// <response code="404">No such recipe for this user.</response>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(RecipeDto), StatusCodes.Status200OK)]
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
            return Ok(await recipeService.GetAsync(userId, id, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
    }
}
