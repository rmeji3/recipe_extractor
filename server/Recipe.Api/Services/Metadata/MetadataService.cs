using Recipe.Api.Common;
using Recipe.Api.Data.App;
using Recipe.Api.Dtos.Import;
using Recipe.Api.Models.Import;

namespace Recipe.Api.Services.Metadata;

public interface IMetadataService
{
    /// <summary>Fetches stage-1 metadata for one post.</summary>
    Task<SavedPostDto> FetchAsync(string userId, Guid savedPostId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches for up to <paramref name="limit"/> pending posts in an import, newest saved
    /// first. Stops early if the platform starts rate-limiting.
    /// </summary>
    Task<MetadataRunDto> FetchPendingAsync(
        string userId, Guid importId, int limit, CancellationToken cancellationToken = default);
}

/// <param name="Attempted">Posts this run touched.</param>
/// <param name="Fetched">Posts that came back with a caption and creator.</param>
/// <param name="Unavailable">Posts the platform no longer serves. Terminal — never retried.</param>
/// <param name="Failed">Transient failures. Still pending, safe to retry.</param>
/// <param name="Remaining">Posts in this import still awaiting stage 1.</param>
/// <param name="StoppedEarly">
/// True when the run aborted because the platform began rate-limiting. Back off before
/// calling again.
/// </param>
public record MetadataRunDto(
    int Attempted,
    int Fetched,
    int Unavailable,
    int Failed,
    int Remaining,
    bool StoppedEarly);

public class MetadataService(
    AppDbContext db,
    IOEmbedClient oembed,
    TimeProvider timeProvider,
    ILogger<MetadataService> logger) : IMetadataService
{
    /// <summary>
    /// Ceiling on one synchronous run. Stage 1 over a full backlog is queue work — this
    /// endpoint exists so the pipeline is usable before the queue lands.
    /// </summary>
    public const int MaxBatch = 200;

    public async Task<SavedPostDto> FetchAsync(
        string userId,
        Guid savedPostId,
        CancellationToken cancellationToken = default)
    {
        var post = await db.SavedPosts
            .FirstOrDefaultAsync(p => p.Id == savedPostId && p.UserId == userId, cancellationToken)
            ?? throw new KeyNotFoundException($"Saved post {savedPostId} was not found.");

        await FetchOneAsync(post, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        return ToDto(post);
    }

    public async Task<MetadataRunDto> FetchPendingAsync(
        string userId,
        Guid importId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var owned = await db.ImportJobs
            .AnyAsync(j => j.Id == importId && j.UserId == userId, cancellationToken);

        if (!owned)
        {
            throw new KeyNotFoundException($"Import {importId} was not found.");
        }

        limit = Math.Clamp(limit, 1, MaxBatch);

        var pending = await db.SavedPosts
            .Where(p => p.ImportJobId == importId
                        && p.Platform == SourcePlatform.TikTok
                        && (p.MetadataStatus == MetadataStatus.Pending
                            || p.MetadataStatus == MetadataStatus.Failed))
            // Newest first, deliberately. Old saves are disproportionately dead — a run
            // over the oldest 30 of a real backlog resolved 11 of 30, against 62% overall.
            // Working backwards from the most recent save puts live, relevant videos in
            // front of the user first and leaves the graveyard for last.
            .OrderByDescending(p => p.SavedAt)
            // Stable tiebreaker. SavedAt is nullable and often absent, and without this
            // the order between equal rows is undefined — successive batches would pick
            // overlapping sets and skip others entirely.
            .ThenBy(p => p.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);

        int attempted = 0, fetched = 0, unavailable = 0, failed = 0;
        var stoppedEarly = false;

        foreach (var post in pending)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                stoppedEarly = true;
                break;
            }

            var (status, rateLimited) = await FetchOneAsync(post, cancellationToken);
            attempted++;

            switch (status)
            {
                case MetadataStatus.Fetched: fetched++; break;
                case MetadataStatus.Unavailable: unavailable++; break;
                default: failed++; break;
            }

            if (rateLimited)
            {
                // Better to stop with work left than to burn through the remaining posts
                // while the platform is refusing, and mark live videos dead.
                logger.LogWarning("Stopping stage 1 early for import {ImportId}: rate-limited", importId);
                stoppedEarly = true;
                break;
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        var remaining = await db.SavedPosts
            .CountAsync(p => p.ImportJobId == importId
                             && p.Platform == SourcePlatform.TikTok
                             && (p.MetadataStatus == MetadataStatus.Pending
                                 || p.MetadataStatus == MetadataStatus.Failed),
                        cancellationToken);

        return new MetadataRunDto(attempted, fetched, unavailable, failed, remaining, stoppedEarly);
    }

    /// <returns>The post's new status, and whether the platform is rate-limiting us.</returns>
    private async Task<(MetadataStatus Status, bool RateLimited)> FetchOneAsync(
        SavedPost post, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        if (post.Platform != SourcePlatform.TikTok)
        {
            // Instagram exports ship the caption and the creator already. There is nothing
            // to fetch, and its oEmbed endpoint needs an app token anyway.
            post.MetadataStatus = MetadataStatus.NotNeeded;
            post.MetadataFetchedAt = now;
            return (post.MetadataStatus, false);
        }

        try
        {
            var result = await oembed.FetchAsync(post.PlatformItemId, cancellationToken);

            if (result is null)
            {
                post.MetadataStatus = MetadataStatus.Unavailable;
                post.MetadataFetchedAt = now;
                return (post.MetadataStatus, false);
            }

            // The caption arrives as oEmbed's "title" and is the whole description.
            if (!string.IsNullOrWhiteSpace(result.Title))
            {
                post.Caption = CaptionText.Normalise([result.Title]);
            }

            post.CreatorHandle ??= Truncate(result.AuthorUniqueId, 128);
            post.CreatorName ??= Truncate(result.AuthorName, 256);
            post.ThumbnailUrl ??= Truncate(result.ThumbnailUrl, 2048);
            post.MetadataStatus = MetadataStatus.Fetched;
            post.MetadataFetchedAt = now;
        }
        catch (OEmbedException ex)
        {
            logger.LogWarning("Stage 1 failed for {ItemId}: {Message}", post.PlatformItemId, ex.Message);
            post.MetadataStatus = MetadataStatus.Failed;
            return (post.MetadataStatus,
                    ex.Message.Contains("rate-limit", StringComparison.OrdinalIgnoreCase));
        }

        return (post.MetadataStatus, false);
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];

    private static SavedPostDto ToDto(SavedPost p) => new(
        p.Id, p.Platform, p.PlatformItemId, p.Url, p.Kind, p.Caption,
        p.CreatorHandle, p.CreatorName, p.Hashtags, p.SavedAt, p.CreatedAt,
        p.MetadataStatus, p.ThumbnailUrl,
        p.ClassificationStatus, p.FoodConfidence, p.ClassifiedBy);
}
