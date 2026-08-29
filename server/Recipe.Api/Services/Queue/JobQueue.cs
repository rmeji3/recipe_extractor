using System.Text.Json;
using System.Text.Json.Serialization;
using StackExchange.Redis;

namespace Recipe.Api.Services.Queue;

/// <summary>What a queued job asks the worker to do.</summary>
/// <remarks>Persisted as a string in Redis, so these names are a stored contract.</remarks>
public enum JobType
{
    /// <summary>Stage 1: fetch caption, creator, and thumbnail for one post.</summary>
    FetchMetadata,

    /// <summary>Judge a batch of an import's pending posts as food or not.</summary>
    Classify,

    /// <summary>Run the extraction cascade for one saved post.</summary>
    Extract
}

/// <param name="Type">What to do.</param>
/// <param name="UserId">Who it belongs to. Every handler re-checks ownership.</param>
/// <param name="TargetId">A saved post id, or an import job id for <see cref="JobType.Classify"/>.</param>
/// <param name="Attempt">1 on first enqueue, incremented on retry.</param>
/// <param name="EnqueuedAt">When it was first queued, for measuring lag.</param>
public record Job(JobType Type, string UserId, Guid TargetId, int Attempt, DateTime EnqueuedAt)
{
    public Job Retry() => this with { Attempt = Attempt + 1 };
}

public interface IJobQueue
{
    Task EnqueueAsync(Job job, CancellationToken cancellationToken = default);

    Task EnqueueManyAsync(IEnumerable<Job> jobs, CancellationToken cancellationToken = default);

    /// <summary>Pops the next job, or null when the queue is empty.</summary>
    Task<Job?> DequeueAsync(CancellationToken cancellationToken = default);

    /// <summary>Jobs waiting, for a progress bar or a dashboard.</summary>
    Task<long> DepthAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A Redis list used as a FIFO queue.
/// </summary>
/// <remarks>
/// Deliberately simple. The work here is retryable and idempotent — every handler is safe
/// to run twice, because extraction updates a row keyed by saved post and stage 1 skips
/// anything already fetched — so a job lost to a crash costs one repeat, not correctness.
/// If that stops being true, this is the seam to put a reliable queue behind.
/// </remarks>
public class RedisJobQueue(IConnectionMultiplexer redis, ILogger<RedisJobQueue> logger) : IJobQueue
{
    private const string Key = "recipe:jobs";

    /// <summary>
    /// Give up after this many attempts. Failures here are mostly platform-side — a video
    /// that will not fetch will not fetch on the fifth try either.
    /// </summary>
    public const int MaxAttempts = 3;

    /// <summary>
    /// Enums as strings, deliberately. These payloads outlive the process that wrote them,
    /// and numeric values would silently change meaning the moment someone reorders
    /// <see cref="JobType"/> — a queued Extract would come back as a FetchMetadata.
    /// </summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task EnqueueAsync(Job job, CancellationToken cancellationToken = default)
    {
        if (job.Attempt > MaxAttempts)
        {
            logger.LogWarning("Dropping {Type} for {TargetId} after {Attempt} attempts",
                job.Type, job.TargetId, job.Attempt);
            return;
        }

        await redis.GetDatabase().ListLeftPushAsync(Key, JsonSerializer.Serialize(job, Json));
    }

    public async Task EnqueueManyAsync(IEnumerable<Job> jobs, CancellationToken cancellationToken = default)
    {
        var values = jobs
            .Where(j => j.Attempt <= MaxAttempts)
            .Select(j => (RedisValue)JsonSerializer.Serialize(j, Json))
            .ToArray();

        if (values.Length > 0)
        {
            await redis.GetDatabase().ListLeftPushAsync(Key, values);
        }
    }

    public async Task<Job?> DequeueAsync(CancellationToken cancellationToken = default)
    {
        var value = await redis.GetDatabase().ListRightPopAsync(Key);

        if (value.IsNullOrEmpty)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Job>((string)value!, Json);
        }
        catch (JsonException ex)
        {
            // A payload this worker cannot read will never become readable. Drop it rather
            // than blocking the queue behind it.
            logger.LogError(ex, "Discarding unreadable job payload");
            return null;
        }
    }

    public async Task<long> DepthAsync(CancellationToken cancellationToken = default) =>
        await redis.GetDatabase().ListLengthAsync(Key);
}
