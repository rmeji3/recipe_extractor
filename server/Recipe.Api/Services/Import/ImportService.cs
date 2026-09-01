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

    public Task<PaginatedResult<SavedPostDto>> ListForReviewAsync(
        string userId,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var query = db.SavedPosts
            .Where(p => p.UserId == userId
                        && p.ClassificationStatus == ClassificationStatus.Uncertain)
            // Most likely food first: the user gets the easy yeses out of the way before
            // the genuinely marginal ones, which is what makes bulk approval quick.
            .OrderByDescending(p => p.FoodConfidence)
            .ThenBy(p => p.Id)
            .Select(p => new SavedPostDto(
                p.Id, p.Platform, p.PlatformItemId, p.Url, p.Kind, p.Caption,
                p.CreatorHandle, p.CreatorName, p.Hashtags, p.SavedAt, p.CreatedAt,
                p.MetadataStatus, p.ThumbnailUrl,
                p.ClassificationStatus, p.FoodConfidence, p.ClassifiedBy));

        return PaginatedResult<SavedPostDto>.CreateAsync(query, pageNumber, pageSize, cancellationToken);
    }

    public async Task<ReviewResultDto> ReviewAsync(
        string userId,
        ReviewDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        var touched = request.Approve.Concat(request.Reject).Distinct().ToList();

        if (touched.Count == 0)
        {
            return new ReviewResultDto(0, 0, await PendingReviewCountAsync(userId, cancellationToken));
        }

        var posts = await db.SavedPosts
            .Where(p => p.UserId == userId && touched.Contains(p.Id))
            .ToListAsync(cancellationToken);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var approve = request.Approve.ToHashSet();
        var reject = request.Reject.ToHashSet();
        var queued = new List<Job>();
        int approved = 0, rejected = 0;

        foreach (var post in posts)
        {
            // Approve wins a conflict: the user meant to keep it, and a wrongly kept post
            // is a line in a cookbook while a wrongly dropped one is invisible.
            if (approve.Contains(post.Id))
            {
                post.ClassificationStatus = ClassificationStatus.Food;
                post.FoodConfidence = 1.0;
                post.ClassifiedBy = "user";
                post.ClassifiedAt = now;
                queued.Add(new Job(JobType.Extract, userId, post.Id, 1, now));
                approved++;
            }
            else if (reject.Contains(post.Id))
            {
                post.ClassificationStatus = ClassificationStatus.NotFood;
                post.FoodConfidence = 0;
                post.ClassifiedBy = "user";
                post.ClassifiedAt = now;
                rejected++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        // Queued after the write: a crash between the two costs a re-review, not a lost
        // decision.
        await queue.EnqueueManyAsync(queued, cancellationToken);

        return new ReviewResultDto(approved, rejected, await PendingReviewCountAsync(userId, cancellationToken));
    }

    private Task<int> PendingReviewCountAsync(string userId, CancellationToken cancellationToken) =>
        db.SavedPosts.CountAsync(
            p => p.UserId == userId && p.ClassificationStatus == ClassificationStatus.Uncertain,
            cancellationToken);

    private static ImportSummaryDto ToSummary(ImportJob job) => new(
        job.Id, job.Platform, job.SubmittedCount, job.ImportedCount, job.DuplicateCount, job.CreatedAt);
}
