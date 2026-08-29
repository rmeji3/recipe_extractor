using Recipe.Api.Common;
using Recipe.Api.Data.App;
using Recipe.Api.Dtos.Import;
using Recipe.Api.Models.Import;
using Recipe.Api.Services.Queue;

namespace Recipe.Api.Services.Import;

public class ImportService(AppDbContext db, IJobQueue queue, TimeProvider timeProvider) : IImportService
{
    public async Task<ImportSummaryDto> CreateAsync(
        string userId,
        CreateImportRequest request,
        CancellationToken cancellationToken = default)
    {
        var submittedCount = request.Posts.Count;

        // Collapse repeats inside the payload first: the same item can appear twice in an
        // export, and a duplicate within the batch is not a duplicate against the database.
        var incoming = request.Posts
            .Where(p => !string.IsNullOrWhiteSpace(p.PlatformItemId))
            .GroupBy(p => p.PlatformItemId, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        var incomingIds = incoming.Select(p => p.PlatformItemId).ToList();

        var existingIds = await db.SavedPosts
            .Where(p => p.UserId == userId
                        && p.Platform == request.Platform
                        && incomingIds.Contains(p.PlatformItemId))
            .Select(p => p.PlatformItemId)
            .ToListAsync(cancellationToken);

        var existing = existingIds.ToHashSet(StringComparer.Ordinal);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var job = new ImportJob
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Platform = request.Platform,
            SubmittedCount = submittedCount,
            CreatedAt = now
        };

        foreach (var post in incoming.Where(p => !existing.Contains(p.PlatformItemId)))
        {
            job.Posts.Add(new SavedPost
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ImportJobId = job.Id,
                Platform = request.Platform,
                PlatformItemId = post.PlatformItemId,
                Url = post.Url,
                Kind = post.Kind,
                Caption = CaptionText.Normalise(post.Captions),
                CreatorHandle = post.CreatorHandle,
                CreatorName = post.CreatorName,
                Hashtags = post.Hashtags ?? [],
                SavedAt = post.SavedAt,
                CreatedAt = now
            });
        }

        job.ImportedCount = job.Posts.Count;
        job.DuplicateCount = submittedCount - job.ImportedCount;

        db.ImportJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);

        await EnqueueFollowUpAsync(job, cancellationToken);

        return ToSummary(job);
    }

    /// <summary>
    /// Queues the work an import implies, so the caller never waits on it.
    ///
    /// TikTok posts need stage 1 before anything can judge or extract them, so those are
    /// queued per post. Classification is queued once for the whole import: it batches a
    /// hundred captions per model call and re-queues itself while work remains.
    /// </summary>
    private async Task EnqueueFollowUpAsync(ImportJob job, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        if (job.Platform == SourcePlatform.TikTok)
        {
            await queue.EnqueueManyAsync(
                job.Posts.Select(p => new Job(JobType.FetchMetadata, job.UserId, p.Id, 1, now)),
                cancellationToken);
        }

        if (job.Posts.Count > 0)
        {
            await queue.EnqueueAsync(new Job(JobType.Classify, job.UserId, job.Id, 1, now), cancellationToken);
        }
    }

    public async Task<ImportSummaryDto> GetAsync(
        string userId,
        Guid importId,
        CancellationToken cancellationToken = default)
    {
        var summary = await db.ImportJobs
            .Where(j => j.Id == importId && j.UserId == userId)
            .Select(j => new ImportSummaryDto(
                j.Id, j.Platform, j.SubmittedCount, j.ImportedCount, j.DuplicateCount, j.CreatedAt))
            .FirstOrDefaultAsync(cancellationToken);

        return summary ?? throw new KeyNotFoundException($"Import {importId} was not found.");
    }

    public Task<PaginatedResult<ImportSummaryDto>> ListAsync(
        string userId,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var query = db.ImportJobs
            .Where(j => j.UserId == userId)
            .OrderByDescending(j => j.CreatedAt)
            .Select(j => new ImportSummaryDto(
                j.Id, j.Platform, j.SubmittedCount, j.ImportedCount, j.DuplicateCount, j.CreatedAt));

        return PaginatedResult<ImportSummaryDto>.CreateAsync(query, pageNumber, pageSize, cancellationToken);
    }

    public async Task<PaginatedResult<SavedPostDto>> ListPostsAsync(
        string userId,
        Guid importId,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var owned = await db.ImportJobs
            .AnyAsync(j => j.Id == importId && j.UserId == userId, cancellationToken);

        if (!owned)
        {
            throw new KeyNotFoundException($"Import {importId} was not found.");
        }

        var query = db.SavedPosts
            .Where(p => p.ImportJobId == importId)
            // The ranked queue: most confidently food first, so real recipes are what the
            // user sees while the uncertain tail is still being worked through.
            .OrderByDescending(p => p.FoodConfidence)
            .ThenByDescending(p => p.SavedAt)
            .ThenBy(p => p.Id)
            .Select(p => new SavedPostDto(
                p.Id, p.Platform, p.PlatformItemId, p.Url, p.Kind, p.Caption,
                p.CreatorHandle, p.CreatorName, p.Hashtags, p.SavedAt, p.CreatedAt,
                p.MetadataStatus, p.ThumbnailUrl,
                p.ClassificationStatus, p.FoodConfidence, p.ClassifiedBy));

        return await PaginatedResult<SavedPostDto>.CreateAsync(query, pageNumber, pageSize, cancellationToken);
    }

    private static ImportSummaryDto ToSummary(ImportJob job) => new(
        job.Id, job.Platform, job.SubmittedCount, job.ImportedCount, job.DuplicateCount, job.CreatedAt);
}
