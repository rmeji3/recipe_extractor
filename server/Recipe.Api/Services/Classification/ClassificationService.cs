using Recipe.Api.Data.App;
using Recipe.Api.Models.Import;

namespace Recipe.Api.Services.Classification;

public interface IClassificationService
{
    /// <summary>
    /// Classifies pending posts in an import, cheapest signal first.
    /// </summary>
    /// <returns>How many posts were resolved, and by which pass.</returns>
    Task<ClassificationRun> ClassifyPendingAsync(
        Guid importJobId, int limit, CancellationToken cancellationToken = default);
}

/// <summary>
/// What one classification run resolved, and by which pass.
///
/// <c>Keyword</c> is free vocabulary matching. <c>Creator</c> is inherited from what the
/// same creator's other posts turned out to be — also free, and more accurate than prompt
/// tuning, because people save recipes from the same few food accounts repeatedly.
/// <c>Model</c> is the batched call, and the only tier that costs anything.
///
/// <c>Food</c>, <c>Uncertain</c>, and <c>NotFood</c> are the resulting tiers;
/// <c>Remaining</c> is what is still pending in this import.
/// </summary>
public record ClassificationRun(
    int Attempted, int Keyword, int Creator, int Model, int Food, int Uncertain, int NotFood, int Remaining);

public class ClassificationService(
    AppDbContext db,
    IClassifierClient classifier,
    TimeProvider timeProvider,
    ILogger<ClassificationService> logger) : IClassificationService
{
    /// <summary>
    /// Captions per model call. A whole library is two or three calls at this size; one
    /// call per post is the expensive version of the same answer.
    /// </summary>
    public const int BatchSize = 100;

    /// <summary>Above this, extract automatically. Below <see cref="UncertainFloor"/>, skip.</summary>
    public const double FoodFloor = 0.75;
    public const double UncertainFloor = 0.4;

    /// <summary>
    /// Food hits by one creator before their other posts inherit the verdict. Two is enough
    /// to be a pattern rather than a coincidence, and the tier it assigns is Uncertain, so a
    /// wrong guess lands in the review pile rather than the cookbook.
    /// </summary>
    private const int CreatorEvidence = 2;

    public async Task<ClassificationRun> ClassifyPendingAsync(
        Guid importJobId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var pending = await db.SavedPosts
            .Where(p => p.ImportJobId == importJobId
                        && p.ClassificationStatus == ClassificationStatus.Pending)
            // Posts with a caption first: they are the ones that can actually be judged,
            // and on the TikTok path a caption means stage 1 has already run.
            .OrderByDescending(p => p.Caption != null)
            .ThenByDescending(p => p.SavedAt)
            .ThenBy(p => p.Id)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(cancellationToken);

        if (pending.Count == 0)
        {
            return new ClassificationRun(0, 0, 0, 0, 0, 0, 0, 0);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        int keyword = 0, creator = 0, model = 0;
        var undecided = new List<SavedPost>();

        // Pass 1 — vocabulary. Free, and resolves a chunk in both directions.
        foreach (var post in pending)
        {
            if (post.MetadataStatus == MetadataStatus.Unavailable)
            {
                // The platform no longer serves it; there will never be anything to judge.
                Assign(post, ClassificationStatus.Unclassifiable, 0, "keyword", now);
                keyword++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(post.Caption))
            {
                // Nothing to judge. Sending an empty caption to the model is worse than
                // useless: asked to classify nothing it answers "food", which on a real
                // backlog marked 350 captionless posts as recipes and cost real money.
                if (post.Platform == SourcePlatform.TikTok
                    && post.MetadataStatus is MetadataStatus.Pending or MetadataStatus.Failed)
                {
                    // Stage 1 has not run yet. Leave it Pending — the caption is coming.
                    continue;
                }

                Assign(post, ClassificationStatus.Unclassifiable, 0, "keyword", now);
                keyword++;
                continue;
            }

            var (verdict, confidence) = FoodKeywords.Judge(post.Caption);

            switch (verdict)
            {
                case FoodKeywords.Verdict.Food:
                    Assign(post, ClassificationStatus.Food, confidence, "keyword", now);
                    keyword++;
                    break;
                case FoodKeywords.Verdict.NotFood:
                    Assign(post, ClassificationStatus.NotFood, 1 - confidence, "keyword", now);
                    keyword++;
                    break;
                default:
                    undecided.Add(post);
                    break;
            }
        }

        // Pass 2 — creator clustering, against verdicts this user already has.
        if (undecided.Count > 0)
        {
            var handles = undecided
                .Where(p => !string.IsNullOrWhiteSpace(p.CreatorHandle))
                .Select(p => p.CreatorHandle!)
                .Distinct()
                .ToList();

            var reputation = await db.SavedPosts
                .Where(p => p.UserId == pending[0].UserId
                            && p.CreatorHandle != null
                            && handles.Contains(p.CreatorHandle)
                            && (p.ClassificationStatus == ClassificationStatus.Food
                                || p.ClassificationStatus == ClassificationStatus.NotFood))
                .GroupBy(p => p.CreatorHandle!)
                .Select(g => new
                {
                    Handle = g.Key,
                    Food = g.Count(p => p.ClassificationStatus == ClassificationStatus.Food),
                    NotFood = g.Count(p => p.ClassificationStatus == ClassificationStatus.NotFood)
                })
                .ToDictionaryAsync(x => x.Handle, x => x, cancellationToken);

            foreach (var post in undecided.ToList())
            {
                if (post.CreatorHandle is null
                    || !reputation.TryGetValue(post.CreatorHandle, out var record))
                {
                    continue;
                }

                if (record.Food >= CreatorEvidence && record.NotFood == 0)
                {
                    // Lands in the review pile, not the cookbook — inherited evidence is
                    // weaker than evidence about this post.
                    Assign(post, ClassificationStatus.Uncertain, 0.6, "creator", now);
                    undecided.Remove(post);
                    creator++;
                }
                else if (record.NotFood >= CreatorEvidence && record.Food == 0)
                {
                    Assign(post, ClassificationStatus.NotFood, 0.2, "creator", now);
                    undecided.Remove(post);
                    creator++;
                }
            }
        }

        // Pass 3 — the batched model call, for whatever is left.
        foreach (var batch in Chunk(undecided, BatchSize))
        {
            var verdicts = await classifier.ClassifyAsync(
                [.. batch.Select(p => new ClassifierItem(p.Caption, p.CreatorHandle))],
                cancellationToken);

            for (var i = 0; i < batch.Count; i++)
            {
                var verdict = i < verdicts.Count ? verdicts[i] : null;

                if (verdict is null)
                {
                    logger.LogWarning("Classifier returned no verdict for index {Index}", i);
                    continue;
                }

                var confidence = verdict.IsFood ? verdict.Confidence : 1 - verdict.Confidence;
                Assign(batch[i], Tier(confidence), confidence, "model", now);
                model++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        var remaining = await db.SavedPosts.CountAsync(
            p => p.ImportJobId == importJobId && p.ClassificationStatus == ClassificationStatus.Pending,
            cancellationToken);

        return new ClassificationRun(
            pending.Count, keyword, creator, model,
            Food: pending.Count(p => p.ClassificationStatus == ClassificationStatus.Food),
            Uncertain: pending.Count(p => p.ClassificationStatus == ClassificationStatus.Uncertain),
            NotFood: pending.Count(p => p.ClassificationStatus == ClassificationStatus.NotFood),
            Remaining: remaining);
    }

    /// <summary>
    /// Three tiers, tuned for precision. A missed recipe is recoverable — the user finds it
    /// under "skipped" — while a cookbook full of memes is not.
    /// </summary>
    private static ClassificationStatus Tier(double confidence) => confidence switch
    {
        >= FoodFloor => ClassificationStatus.Food,
        >= UncertainFloor => ClassificationStatus.Uncertain,
        _ => ClassificationStatus.NotFood
    };

    private static void Assign(
        SavedPost post, ClassificationStatus status, double confidence, string by, DateTime now)
    {
        post.ClassificationStatus = status;
        post.FoodConfidence = Math.Round(Math.Clamp(confidence, 0, 1), 2);
        post.ClassifiedBy = by;
        post.ClassifiedAt = now;
    }

    private static IEnumerable<List<T>> Chunk<T>(List<T> source, int size)
    {
        for (var i = 0; i < source.Count; i += size)
        {
            yield return source.GetRange(i, Math.Min(size, source.Count - i));
        }
    }
}
