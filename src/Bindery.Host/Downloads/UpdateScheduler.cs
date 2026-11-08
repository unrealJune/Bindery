using Bindery.Host.Configuration;
using Bindery.Host.Data;
using Bindery.Host.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bindery.Host.Downloads;

/// <summary>
/// Re-checks filed books for new chapters on a schedule, so an ongoing serial stays current
/// without anyone pressing a button.
/// </summary>
/// <remarks>
/// The whole job is picking <em>which</em> books are due; the actual work is
/// <see cref="DownloadService.EnqueueUpdateAsync"/>, the same path the book page's manual
/// refresh already uses. That reuse is the point — a scheduled update and a hand-triggered
/// one produce an identical job, so there is no second code path that can rot.
///
/// "Last checked" is derived from the Jobs table rather than stored on the book. A book that
/// has never been updated has no update job, and its AddedAt stands in. This keeps the
/// scheduler free of any schema change, which matters because the library on disk is the
/// source of truth and the database is meant to stay rebuildable from a rescan.
/// </remarks>
public sealed class UpdateScheduler(
    IServiceScopeFactory scopes,
    IOptions<BinderyOptions> options,
    ILogger<UpdateScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value.Updates;

        if (!settings.Enabled)
        {
            logger.LogInformation("update scheduler disabled");
            return;
        }

        logger.LogInformation(
            "update scheduler starting: every book re-checked about every {Interval}, sweeping every {Sweep}, at most {Max} per sweep",
            settings.Interval, settings.SweepInterval, settings.MaxPerSweep);

        // A short random offset before the first sweep. Without it every restart — and a
        // rollout restarts the pod — would queue the same books at the same instant.
        await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(20, 90)), stoppingToken);

        using var timer = new PeriodicTimer(settings.SweepInterval);

        do
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A sweep that throws must not take the loop with it, or one bad book stops
                // updates for the whole library until the next restart.
                logger.LogError(ex, "update sweep failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value.Updates;

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BinderyDbContext>();
        var downloads = scope.ServiceProvider.GetRequiredService<DownloadService>();
        var registry = scope.ServiceProvider.GetRequiredService<PluginRegistry>();

        var usable = registry.Usable.Select(descriptor => descriptor.Name).ToHashSet(StringComparer.Ordinal);

        if (usable.Count == 0)
        {
            return;
        }

        var due = await DueBooksAsync(db, usable, settings, cancellationToken);

        foreach (var book in due)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await downloads.EnqueueUpdateAsync(book.Id, requestedBy: null, cancellationToken);

            if (result.Job is null)
            {
                // Not an error: a book can become ineligible between the query and here.
                logger.LogDebug("skipped scheduled update of {Title}: {Problem}", book.Title, result.Problem);
                continue;
            }

            logger.LogInformation("scheduled update of {Title} queued as job {Job}", book.Title, result.Job.Id);
        }
    }

    /// <summary>
    /// Books whose most recent job is older than the configured interval, oldest first, and
    /// which have nothing in flight.
    /// </summary>
    private static async Task<List<BookEntity>> DueBooksAsync(
        BinderyDbContext db,
        HashSet<string> usablePlugins,
        UpdateOptions settings,
        CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow - settings.Interval;

        // Anything unfinished counts as in flight, so a book stuck retrying is never piled
        // on by the scheduler.
        var active = await db.Jobs
            .Where(job => job.Status == JobStatus.Queued || job.Status == JobStatus.Running)
            .Select(job => job.BookId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var busy = active.Where(id => id.HasValue).Select(id => id!.Value).ToHashSet();

        var candidates = await db.Books
            .Where(book => book.SourceUrl != null && book.SourcePlugin != null)
            .Select(book => new
            {
                Book = book,
                LastAttempt = db.Jobs
                    .Where(job => job.BookId == book.Id)
                    .Max(job => (DateTimeOffset?)job.CreatedAt),
            })
            .ToListAsync(cancellationToken);

        return candidates
            .Where(row => usablePlugins.Contains(row.Book.SourcePlugin!))
            .Where(row => !busy.Contains(row.Book.Id))
            .Where(row => (row.LastAttempt ?? row.Book.AddedAt) <= cutoff)
            .OrderBy(row => row.LastAttempt ?? row.Book.AddedAt)
            .Take(Math.Max(1, settings.MaxPerSweep))
            .Select(row => row.Book)
            .ToList();
    }
}
