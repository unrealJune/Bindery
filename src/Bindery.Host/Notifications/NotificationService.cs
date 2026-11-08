using Bindery.Host.Data;
using Microsoft.EntityFrameworkCore;

namespace Bindery.Host.Notifications;

public sealed record NotificationPreferences(
    string? Email,
    bool EmailEnabled,
    IReadOnlySet<string> Muted)
{
    public bool IsMuted(string category) => Muted.Contains(category);
}

/// <summary>
/// The front door of the notification subsystem: preferences, and the outbox.
/// </summary>
/// <remarks>
/// <see cref="EnqueueAsync"/> writes a row and returns. Delivery is
/// <see cref="NotificationWorker"/>'s job, so a sync, a download, or a request never waits
/// on a mail server and never fails because one is down.
///
/// Nothing a plugin says reaches here directly. A plugin reports state; the host decides
/// whether that is worth a message and writes it. That is the property that keeps a
/// compromised plugin image from becoming a mail cannon pointed at your household.
/// </remarks>
public sealed class NotificationService(BinderyDbContext db, ILogger<NotificationService> logger)
{
    /// <summary>
    /// A person's preferences, creating them on first sight with the address their identity
    /// provider reported. Called from pages, where the claims are at hand; the worker only
    /// ever reads what was stored.
    /// </summary>
    public async Task<NotificationPreferences> EnsureAsync(
        string subject,
        string? claimedEmail,
        CancellationToken cancellationToken)
    {
        var row = await db.NotificationPreferences.FirstOrDefaultAsync(p => p.Subject == subject, cancellationToken);

        if (row is null)
        {
            row = new NotificationPreferenceEntity
            {
                Subject = subject,
                Email = string.IsNullOrWhiteSpace(claimedEmail) ? null : claimedEmail.Trim(),
                MutedCategories = string.Join(',',
                    NotificationCategories.All.Where(category => !category.DefaultOn).Select(category => category.Key))
            };

            db.NotificationPreferences.Add(row);
            await db.SaveChangesAsync(cancellationToken);
        }

        return ToView(row);
    }

    public async Task<NotificationPreferences?> GetAsync(string subject, CancellationToken cancellationToken)
    {
        var row = await db.NotificationPreferences.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Subject == subject, cancellationToken);

        return row is null ? null : ToView(row);
    }

    public async Task SaveAsync(
        string subject,
        string? email,
        bool emailEnabled,
        IEnumerable<string> enabledCategories,
        CancellationToken cancellationToken)
    {
        var row = await db.NotificationPreferences.FirstOrDefaultAsync(p => p.Subject == subject, cancellationToken);

        if (row is null)
        {
            row = new NotificationPreferenceEntity { Subject = subject };
            db.NotificationPreferences.Add(row);
        }

        var enabled = enabledCategories.ToHashSet(StringComparer.Ordinal);

        row.Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        row.EmailEnabled = emailEnabled;
        row.MutedCategories = string.Join(',',
            NotificationCategories.All.Where(category => !enabled.Contains(category.Key)).Select(category => category.Key));
        row.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Queues a message. Never throws for delivery reasons and never blocks on delivery.
    /// </summary>
    /// <remarks>
    /// A muted category is still written, as <see cref="NotificationStatus.Suppressed"/>, so
    /// the person can see on their own page what Bindery would have told them. Text is plain
    /// and stays plain: subjects are stripped of line breaks here, because they are built
    /// from library data and a newline in a mail header is a header injection.
    /// </remarks>
    public async Task<NotificationEntity> EnqueueAsync(
        string recipient,
        string category,
        string source,
        string subject,
        string body,
        CancellationToken cancellationToken)
    {
        var preferences = await GetAsync(recipient, cancellationToken);
        var muted = preferences is not null && preferences.IsMuted(category) && category != NotificationCategories.Test;

        var entity = new NotificationEntity
        {
            Recipient = recipient,
            Category = category,
            Source = source,
            Subject = Truncate(SingleLine(subject), 300),
            Body = body,
            Status = muted ? NotificationStatus.Suppressed : NotificationStatus.Pending,
            LastError = muted ? "muted" : null
        };

        db.Notifications.Add(entity);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "queued {Category} notification from {Source}{Muted}", category, source, muted ? " (muted)" : string.Empty);

        return entity;
    }

    public async Task<IReadOnlyList<NotificationEntity>> RecentAsync(
        string recipient,
        int limit,
        CancellationToken cancellationToken) =>
        await db.Notifications.AsNoTracking()
            .Where(n => n.Recipient == recipient)
            .OrderByDescending(n => n.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

    internal static NotificationPreferences ToView(NotificationPreferenceEntity row) =>
        new(
            row.Email,
            row.EmailEnabled,
            row.MutedCategories
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.Ordinal));

    internal static string SingleLine(string value) =>
        string.Join(' ', (value ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();

    private static string Truncate(string value, int limit) =>
        value.Length <= limit ? value : value[..(limit - 1)] + "…";
}
