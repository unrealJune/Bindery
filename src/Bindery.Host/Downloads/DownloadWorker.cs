using Bindery.Host.Configuration;
using Bindery.Host.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bindery.Host.Downloads;

/// <summary>
/// The background half of the download pipeline: a fixed pool of consumers, plus a
/// scheduler that requeues work the database says is due.
/// </summary>
/// <remarks>
/// The database is authoritative. At boot, anything left Queued or Running from a previous
/// process is requeued — a pod restarting mid-download must not silently lose the job.
/// </remarks>
public sealed class DownloadWorker(
    DownloadQueue queue,
    IServiceScopeFactory scopes,
    IOptions<BinderyOptions> options,
    ILogger<DownloadWorker> logger) : BackgroundService
{
    private static readonly TimeSpan SchedulerInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var concurrency = Math.Max(1, options.Value.Downloads.Concurrency);
        logger.LogInformation("download worker starting with {Concurrency} slot(s)", concurrency);

        await RequeueOrphansAsync(stoppingToken);
        SweepStaging();

        var consumers = Enumerable
            .Range(0, concurrency)
            .Select(index => ConsumeAsync(index, stoppingToken))
            .Append(ScheduleAsync(stoppingToken));

        await Task.WhenAll(consumers);
    }

    private async Task ConsumeAsync(int slot, CancellationToken stoppingToken)
    {
        await foreach (var jobId in queue.ReadAllAsync(stoppingToken))
        {
            using var tracked = queue.Track(jobId, stoppingToken);

            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var runner = scope.ServiceProvider.GetRequiredService<DownloadRunner>();
                await runner.RunAsync(jobId, tracked.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("slot {Slot} stopping with job {Job} in flight", slot, jobId);
                return;
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("job {Job} was cancelled", jobId);
            }
            catch (Exception ex)
            {
                // A consumer that dies takes a concurrency slot with it for the process's
                // lifetime, so nothing is allowed past here.
                logger.LogError(ex, "slot {Slot} failed on job {Job}", slot, jobId);
            }
            finally
            {
                queue.Release(jobId);
            }
        }
    }

    /// <summary>
    /// Picks up retries whose backoff has elapsed, and anything queued that the in-memory
    /// channel does not know about.
    /// </summary>
    private async Task ScheduleAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SchedulerInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<BinderyDbContext>();
                var now = DateTimeOffset.UtcNow;

                var due = await db.Jobs
                    .Where(job => job.Status == JobStatus.Queued
                                  && (job.NextAttemptAt == null || job.NextAttemptAt <= now))
                    .OrderBy(job => job.CreatedAt)
                    .Select(job => job.Id)
                    .Take(100)
                    .ToListAsync(stoppingToken);

                foreach (var jobId in due.Where(id => !queue.IsRunning(id)))
                {
                    await queue.EnqueueAsync(jobId, stoppingToken);
                }

                await PruneHistoryAsync(db, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "download scheduler pass failed");
            }
        }
    }

    private async Task RequeueOrphansAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<BinderyDbContext>();

            var orphans = await db.Jobs
                .Where(job => job.Status == JobStatus.Running)
                .ToListAsync(stoppingToken);

            foreach (var job in orphans)
            {
                logger.LogWarning("job {Job} was running when the process stopped; requeueing", job.Id);
                job.Status = JobStatus.Queued;
                job.Message = "Requeued after restart";
                job.Percent = 0;
            }

            if (orphans.Count > 0)
            {
                await db.SaveChangesAsync(stoppingToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "could not requeue in-flight jobs after restart");
        }
    }

    private async Task PruneHistoryAsync(BinderyDbContext db, CancellationToken stoppingToken)
    {
        var limit = options.Value.Downloads.HistoryLimit;

        if (limit <= 0)
        {
            return;
        }

        var total = await db.Jobs.CountAsync(stoppingToken);

        if (total <= limit)
        {
            return;
        }

        var excess = await db.Jobs
            .Where(job => job.Status != JobStatus.Queued && job.Status != JobStatus.Running)
            .OrderByDescending(job => job.CreatedAt)
            .Skip(limit)
            .ToListAsync(stoppingToken);

        if (excess.Count == 0)
        {
            return;
        }

        db.Jobs.RemoveRange(excess);
        await db.SaveChangesAsync(stoppingToken);
        logger.LogInformation("pruned {Count} old job record(s)", excess.Count);
    }

    /// <summary>Removes partially fetched artifacts left by a previous process.</summary>
    private void SweepStaging()
    {
        var staging = Path.Combine(options.Value.DataPath, "staging");

        try
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            Directory.CreateDirectory(staging);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "could not clear the staging directory at {Path}", staging);
        }
    }
}
