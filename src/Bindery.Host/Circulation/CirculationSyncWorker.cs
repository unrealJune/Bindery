using Bindery.Host.Configuration;
using Bindery.Host.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bindery.Host.Circulation;

/// <summary>
/// Keeps every connected library account current, so a hold becoming ready is noticed
/// without anyone opening the page.
/// </summary>
/// <remarks>
/// Picking <em>which</em> accounts are due is all this does; the sync itself is
/// <see cref="CirculationService.SyncAsync(CirculationAccountEntity, CancellationToken)"/>,
/// the same path the "Sync now" button takes. An account whose credentials the library
/// refused is skipped until the person re-enters them: retrying a wrong PIN every half hour
/// is how a library card gets locked.
/// </remarks>
public sealed class CirculationSyncWorker(
    IServiceScopeFactory scopes,
    IOptions<BinderyOptions> options,
    ILogger<CirculationSyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value.Circulation;

        if (!settings.SyncEnabled)
        {
            logger.LogInformation("circulation sync disabled");
            return;
        }

        // Jitter, as the update scheduler does: a rollout must not log every card in at once.
        await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(15, 60)), stoppingToken);

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
                logger.LogError(ex, "circulation sweep failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value.Circulation;
        var cutoff = DateTimeOffset.UtcNow - settings.SyncInterval;

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BinderyDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<CirculationService>();

        var due = await db.CirculationAccounts
            .Where(account => !account.NeedsAttention && (account.LastAttemptAt == null || account.LastAttemptAt <= cutoff))
            .OrderBy(account => account.LastAttemptAt)
            .Take(Math.Max(1, settings.MaxPerSweep))
            .ToListAsync(cancellationToken);

        foreach (var account in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await service.SyncAsync(account, cancellationToken);

            if (!result.Succeeded)
            {
                logger.LogInformation("could not sync a {Plugin} account: {Problem}", account.Plugin, result.Problem);
            }
        }
    }
}
