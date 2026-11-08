using Bindery.Host.Configuration;
using Bindery.Host.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bindery.Host.Notifications;

/// <summary>
/// Drains the notification outbox.
/// </summary>
/// <remarks>
/// Preferences are read at delivery time, not at queue time, so muting a category or
/// clearing an address stops messages already in the queue. A failure is retried with
/// exponential backoff, and the schedule lives in the row, so a restart loses nothing.
/// </remarks>
public sealed class NotificationWorker(
    IServiceScopeFactory scopes,
    IEnumerable<INotificationChannel> channels,
    IOptions<BinderyOptions> options,
    ILogger<NotificationWorker> logger) : BackgroundService
{
    private const int BatchSize = 25;

    private readonly NotificationOptions _options = options.Value.Notifications;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!channels.Any(channel => channel.IsConfigured))
        {
            logger.LogInformation(
                "no notification channel is configured; messages will be recorded and marked suppressed. " +
                "Set Bindery:Notifications:Email to deliver them");
        }

        using var timer = new PeriodicTimer(_options.PollInterval);

        do
        {
            try
            {
                await DrainAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "notification drain failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One pass over the outbox. Public so tests can drive delivery deterministically.</summary>
    public async Task<int> DrainAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BinderyDbContext>();
        var now = DateTimeOffset.UtcNow;

        var due = await db.Notifications
            .Where(n => n.Status == NotificationStatus.Pending && (n.NextAttemptAt == null || n.NextAttemptAt <= now))
            .OrderBy(n => n.CreatedAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        if (due.Count == 0)
        {
            return 0;
        }

        var recipients = due.Select(n => n.Recipient).Distinct().ToList();
        var preferences = (await db.NotificationPreferences.AsNoTracking()
                .Where(p => recipients.Contains(p.Subject))
                .ToListAsync(cancellationToken))
            .ToDictionary(p => p.Subject, NotificationService.ToView, StringComparer.Ordinal);

        var email = channels.FirstOrDefault(channel => channel.Name == "email" && channel.IsConfigured);
        var delivered = 0;

        foreach (var notification in due)
        {
            var reason = preferences.TryGetValue(notification.Recipient, out var prefs)
                ? WhySuppressed(notification, prefs, email)
                : "no notification preferences for this person";

            if (reason is not null)
            {
                notification.Status = NotificationStatus.Suppressed;
                notification.LastError = reason;
                continue;
            }

            notification.Attempts += 1;

            try
            {
                await email!.SendAsync(
                    new OutgoingMessage(prefs!.Email!, notification.Subject, notification.Body), cancellationToken);

                notification.Status = NotificationStatus.Sent;
                notification.SentAt = DateTimeOffset.UtcNow;
                notification.LastError = null;
                notification.NextAttemptAt = null;
                delivered++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // The exception text can name the server, never the password; still, keep
                // only the first line of it.
                var message = NotificationService.SingleLine(ex.Message);
                notification.LastError = message.Length > 480 ? message[..480] : message;

                if (notification.Attempts >= _options.MaxAttempts)
                {
                    notification.Status = NotificationStatus.Failed;
                    notification.NextAttemptAt = null;
                    logger.LogWarning("gave up delivering a {Category} notification: {Error}", notification.Category, message);
                }
                else
                {
                    var delay = TimeSpan.FromTicks(_options.RetryDelay.Ticks * (long)Math.Pow(2, notification.Attempts - 1));
                    notification.NextAttemptAt = DateTimeOffset.UtcNow + delay;
                    logger.LogInformation("delivery failed; retrying in {Delay}: {Error}", delay, message);
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await PruneAsync(db, cancellationToken);
        return delivered;
    }

    private static string? WhySuppressed(
        NotificationEntity notification,
        NotificationPreferences prefs,
        INotificationChannel? email)
    {
        if (email is null)
        {
            return "email delivery is not configured on this server";
        }

        if (!prefs.EmailEnabled)
        {
            return "email is switched off";
        }

        if (!EmailChannel.IsValidAddress(prefs.Email))
        {
            return "no email address on file";
        }

        if (notification.Category != NotificationCategories.Test && prefs.IsMuted(notification.Category))
        {
            return "muted";
        }

        return null;
    }

    private async Task PruneAsync(BinderyDbContext db, CancellationToken cancellationToken)
    {
        var finished = db.Notifications.Where(n => n.Status != NotificationStatus.Pending);
        var excess = await finished.CountAsync(cancellationToken) - _options.HistoryLimit;

        if (excess <= 0)
        {
            return;
        }

        var stale = await finished.OrderBy(n => n.CreatedAt).Take(excess).ToListAsync(cancellationToken);
        db.Notifications.RemoveRange(stale);
        await db.SaveChangesAsync(cancellationToken);
    }
}
