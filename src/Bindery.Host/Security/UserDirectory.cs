using System.Collections.Concurrent;
using Bindery.Host.Data;
using Microsoft.EntityFrameworkCore;

namespace Bindery.Host.Security;

/// <summary>
/// Process-wide memory for <see cref="UserDirectory"/>: when each person was last written
/// down, and who the founder is once known.
/// </summary>
/// <remarks>
/// A singleton so that "seen" is recorded at most once an interval per person rather than
/// once per request — the transformation that calls it runs on every authenticated request,
/// and a write per request would turn browsing into a SQLite write storm.
/// </remarks>
public sealed class UserPresence
{
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _touched = new(StringComparer.Ordinal);
    private volatile string? _founder;

    public SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>
    /// Cached once found. Nobody can displace the founder: a newcomer's first-seen time is
    /// always later.
    /// </summary>
    public string? Founder
    {
        get => _founder;
        set => _founder = value;
    }

    public bool IsDue(string subject, DateTimeOffset now) =>
        !_touched.TryGetValue(subject, out var at) || now - at > TouchInterval;

    public void MarkTouched(string subject, DateTimeOffset now) => _touched[subject] = now;
}

/// <summary>
/// The register of people who have used this installation.
/// </summary>
/// <remarks>
/// The identity provider owns accounts; this owns nothing but the answer to two questions
/// the host needs: who is the founder (the earliest person seen, who inherits every book
/// from before shelves existed), and what to call a subject when showing it.
/// </remarks>
public sealed class UserDirectory(BinderyDbContext db, UserPresence presence, ILogger<UserDirectory> logger)
{
    /// <summary>The earliest person ever seen here, or null on an installation nobody has used.</summary>
    public async Task<string?> FounderAsync(CancellationToken cancellationToken)
    {
        if (presence.Founder is { } known)
        {
            return known;
        }

        var founder = await db.Users
            .AsNoTracking()
            .OrderBy(user => user.FirstSeenAt)
            .ThenBy(user => user.Subject)
            .Select(user => user.Subject)
            .FirstOrDefaultAsync(cancellationToken);

        if (founder is not null)
        {
            presence.Founder = founder;
        }

        return founder;
    }

    /// <summary>
    /// Records that a person is here. True only the first time they ever are.
    /// </summary>
    public async Task<bool> TouchAsync(
        string subject,
        string? displayName,
        string? email,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        if (!presence.IsDue(subject, now))
        {
            return false;
        }

        await presence.Gate.WaitAsync(cancellationToken);

        try
        {
            if (!presence.IsDue(subject, now))
            {
                return false;
            }

            var user = await db.Users.FirstOrDefaultAsync(candidate => candidate.Subject == subject, cancellationToken);
            var isNew = user is null;

            if (user is null)
            {
                user = new UserEntity { Subject = subject, FirstSeenAt = now };
                db.Users.Add(user);
            }

            user.LastSeenAt = now;
            user.DisplayName = Clip(displayName, 200) ?? user.DisplayName;
            user.Email = Clip(email, 320) ?? user.Email;

            await db.SaveChangesAsync(cancellationToken);
            presence.MarkTouched(subject, now);

            if (isNew)
            {
                logger.LogInformation("first sign-in by {Subject}", subject);
            }

            return isNew;
        }
        catch (DbUpdateException ex)
        {
            // Two hosts, or a test, racing the same first sign-in. The row exists either way.
            logger.LogDebug(ex, "could not record {Subject}", subject);
            return false;
        }
        finally
        {
            presence.Gate.Release();
        }
    }

    private static string? Clip(string? value, int limit)
    {
        var trimmed = value?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length <= limit ? trimmed : trimmed[..limit];
    }
}
