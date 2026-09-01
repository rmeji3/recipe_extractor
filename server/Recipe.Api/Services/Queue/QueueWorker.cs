using Recipe.Api.Services.Classification;
using Recipe.Api.Services.Metadata;
using Recipe.Api.Services.Recipes;

namespace Recipe.Api.Services.Queue;

/// <summary>
/// Drains the job queue in the background, so a bulk import is never something the user
/// waits on.
/// </summary>
/// <remarks>
/// One job at a time, with a jittered pause between them. Throughput is not the goal —
/// getting blocked by a platform partway through a backlog is the failure mode that kills
/// this feature, and a queue that finishes in eight minutes instead of four is a trade
/// worth making every time.
///
/// Each job runs in its own DI scope: a <c>DbContext</c> is request-scoped, and capturing
/// one in work that outlives the request is the classic way to corrupt it.
/// </remarks>
public class QueueWorker(
    IJobQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<QueueWorker> logger) : BackgroundService
{
    /// <summary>Pause when the queue is empty. Long enough to be idle, short enough to feel prompt.</summary>
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2);

    /// <summary>Base pause between jobs; jitter up to the same again is added on top.</summary>
    private static readonly TimeSpan WorkDelay = TimeSpan.FromMilliseconds(400);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Queue worker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            Job? job;

            try
            {
                job = await queue.DequeueAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                // Redis being down must not kill the worker; the API keeps serving and the
                // queue drains when it comes back.
                logger.LogError(ex, "Could not read from the job queue");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                continue;
            }

            if (job is null)
            {
                await Task.Delay(IdleDelay, stoppingToken);
                continue;
            }

            await RunAsync(job, stoppingToken);

            await Task.Delay(
                WorkDelay + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 400)),
                stoppingToken);
        }
    }

    private async Task RunAsync(Job job, CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var services = scope.ServiceProvider;

        try
        {
            switch (job.Type)
            {
                case JobType.FetchMetadata:
                    await services.GetRequiredService<IMetadataService>()
                        .FetchAsync(job.UserId, job.TargetId, stoppingToken);
                    break;

                case JobType.Classify:
                    await RunClassifyAsync(services, job, stoppingToken);
                    break;

                case JobType.Extract:
                    // ProcessAsync, not ExtractAsync: a queued post may still need stage 1
                    // before its video can be located at all.
                    await services.GetRequiredService<IRecipeService>()
                        .ProcessAsync(job.UserId, job.TargetId, stoppingToken);
                    break;
            }
        }
        catch (KeyNotFoundException)
        {
            // The post or import was deleted between enqueue and now. Nothing to retry.
            logger.LogInformation("{Type} target {TargetId} no longer exists", job.Type, job.TargetId);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down. Put it back so it is not lost.
            await queue.EnqueueAsync(job, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Type} failed for {TargetId} on attempt {Attempt}",
                job.Type, job.TargetId, job.Attempt);
            await queue.EnqueueAsync(job.Retry(), stoppingToken);
        }
    }

    /// <summary>
    /// Classifies a slice of an import, then re-queues itself while work remains, so one
    /// job never grows into an unbounded unit of work.
    /// </summary>
    private async Task RunClassifyAsync(IServiceProvider services, Job job, CancellationToken stoppingToken)
    {
        var classification = services.GetRequiredService<IClassificationService>();
        var run = await classification.ClassifyPendingAsync(
            job.TargetId, ClassificationService.BatchSize, stoppingToken);

        logger.LogInformation(
            "Classified {Attempted} of import {ImportId}: {Food} food, {Uncertain} uncertain, "
            + "{NotFood} not food ({Keyword} by keyword, {Creator} by creator, {Model} by model); "
            + "{Remaining} remaining",
            run.Attempted, job.TargetId, run.Food, run.Uncertain, run.NotFood,
            run.Keyword, run.Creator, run.Model, run.Remaining);

        // Re-queue only while the run is actually resolving posts. "Remaining > 0" alone
        // spins forever once the only posts left are waiting on stage 1: they are selected,
        // skipped, and left Pending, so the job would re-queue itself unchanged in a tight
        // loop. Progress, not backlog, is the condition for continuing.
        var progressed = run.Keyword + run.Creator + run.Model > 0;

        if (run.Remaining > 0 && progressed)
        {
            // Attempt stays at 1: this is continued work, not a retry of failed work.
            await queue.EnqueueAsync(job, stoppingToken);
        }
        else if (run.Remaining > 0)
        {
            logger.LogInformation(
                "Import {ImportId} has {Remaining} posts awaiting stage 1; classification "
                + "will resume when their captions arrive",
                job.TargetId, run.Remaining);
        }
    }
}
