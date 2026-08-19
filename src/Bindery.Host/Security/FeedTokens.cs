using System.Security.Cryptography;
using System.Text;
using Bindery.Host.Data;
using Microsoft.EntityFrameworkCore;

namespace Bindery.Host.Security;

public sealed record IssuedToken(FeedTokenEntity Entity, string Secret);

/// <summary>
/// Long-lived credentials for ereaders.
/// </summary>
/// <remarks>
/// Ereader apps cannot complete an OAuth redirect, so OPDS needs a credential a device can
/// hold. These are it: issued per device from the UI, stored hashed, shown once, and
/// revocable individually so losing a tablet costs one token rather than a password change.
/// They grant the catalog and nothing else — never the web UI.
/// </remarks>
public sealed class FeedTokenService(BinderyDbContext db, ILogger<FeedTokenService> logger)
{
    private const int TokenBytes = 32;

    public async Task<IssuedToken> IssueAsync(string name, string subject, CancellationToken cancellationToken)
    {
        var secret = Base64Url(RandomNumberGenerator.GetBytes(TokenBytes));

        var entity = new FeedTokenEntity
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Unnamed device" : name.Trim(),
            TokenHash = Hash(secret),
            Prefix = secret[..8],
            Subject = subject
        };

        db.FeedTokens.Add(entity);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("issued feed token {Name} ({Prefix}…) for {Subject}", entity.Name, entity.Prefix, subject);
        return new IssuedToken(entity, secret);
    }

    /// <summary>
    /// Resolves a presented token, or null.
    /// </summary>
    /// <remarks>
    /// The lookup is by hash, so the database never holds anything usable. The comparison
    /// is still done in constant time: an index lookup on a hash leaks nothing, but the
    /// habit is worth keeping where a future refactor might reintroduce a real comparison.
    /// </remarks>
    public async Task<FeedTokenEntity?> ValidateAsync(string presented, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(presented) || presented.Length > 200)
        {
            return null;
        }

        var hash = Hash(presented);

        var candidate = await db.FeedTokens
            .FirstOrDefaultAsync(token => token.TokenHash == hash && token.RevokedAt == null, cancellationToken);

        if (candidate is null)
        {
            return null;
        }

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(candidate.TokenHash),
                Encoding.UTF8.GetBytes(hash)))
        {
            return null;
        }

        // Written at most once a minute: an ereader that polls the catalog should not turn
        // "last used" into a write per request.
        if (candidate.LastUsedAt is null || DateTimeOffset.UtcNow - candidate.LastUsedAt > TimeSpan.FromMinutes(1))
        {
            candidate.LastUsedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }

        return candidate;
    }

    public async Task<bool> RevokeAsync(Guid id, CancellationToken cancellationToken)
    {
        var token = await db.FeedTokens.FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (token is null || token.RevokedAt is not null)
        {
            return false;
        }

        token.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("revoked feed token {Name} ({Prefix}…)", token.Name, token.Prefix);
        return true;
    }

    public Task<List<FeedTokenEntity>> ListAsync(CancellationToken cancellationToken) =>
        db.FeedTokens
            .AsNoTracking()
            .OrderByDescending(token => token.CreatedAt)
            .ToListAsync(cancellationToken);

    private static string Hash(string value) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
