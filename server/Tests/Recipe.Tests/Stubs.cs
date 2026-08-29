using System.Collections.Concurrent;
using Recipe.Api.Services.Classification;
using Recipe.Api.Services.Queue;

namespace Recipe.Tests;

/// <summary>
/// An in-memory job queue. Tests drain it deliberately rather than racing a background
/// worker, so queueing behaviour is asserted directly instead of by waiting.
/// </summary>
public class StubJobQueue : IJobQueue
{
    private readonly ConcurrentQueue<Job> _jobs = new();

    /// <summary>Everything ever enqueued, including jobs already dequeued.</summary>
    public List<Job> Enqueued { get; } = [];

    public Task EnqueueAsync(Job job, CancellationToken cancellationToken = default)
    {
        _jobs.Enqueue(job);
        lock (Enqueued) { Enqueued.Add(job); }
        return Task.CompletedTask;
    }

    public Task EnqueueManyAsync(IEnumerable<Job> jobs, CancellationToken cancellationToken = default)
    {
        foreach (var job in jobs)
        {
            EnqueueAsync(job, cancellationToken);
        }
        return Task.CompletedTask;
    }

    public Task<Job?> DequeueAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_jobs.TryDequeue(out var job) ? job : null);

    public Task<long> DepthAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult((long)_jobs.Count);

    public void Clear()
    {
        while (_jobs.TryDequeue(out _)) { }
        lock (Enqueued) { Enqueued.Clear(); }
    }
}

/// <summary>Stands in for the sidecar's batched classifier.</summary>
public class StubClassifier : IClassifierClient
{
    /// <summary>Maps a caption to a verdict. Defaults to confident food.</summary>
    public Func<ClassifierItem, ClassifierVerdict>? Respond { get; set; }

    /// <summary>
    /// Returns no verdicts, which is how the real client reports an outage — it swallows
    /// the failure so the batch stays Pending and is retried, rather than being written
    /// off as not-food.
    /// </summary>
    public bool ReturnEmpty { get; set; }

    public List<int> BatchSizes { get; } = [];

    public Task<IReadOnlyList<ClassifierVerdict>> ClassifyAsync(
        IReadOnlyList<ClassifierItem> items, CancellationToken cancellationToken = default)
    {
        BatchSizes.Add(items.Count);

        if (ReturnEmpty)
        {
            return Task.FromResult<IReadOnlyList<ClassifierVerdict>>([]);
        }

        IReadOnlyList<ClassifierVerdict> verdicts =
            [.. items.Select(i => Respond?.Invoke(i) ?? new ClassifierVerdict(true, 0.9))];

        return Task.FromResult(verdicts);
    }
}
