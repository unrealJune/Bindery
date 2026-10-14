using Bindery.Host.Data;
using Microsoft.EntityFrameworkCore;

namespace Bindery.Host.Opds;

/// <summary>
/// Reading positions, per (book, feed token). A push from a reader overwrites whatever
/// that same token last pushed; tokens never see each other's positions.
/// </summary>
/// <remarks>
/// There is deliberately no history: the newest position wins, matching what every
/// ereader sync service does and keeping the table the size of the library, not the
/// size of the library times its reading history.
/// </remarks>
public sealed class ReadProgressService(BinderyDbContext db)
{
    public async Task<ReadProgressEntity?> GetAsync(Guid bookId, Guid tokenId, CancellationToken cancellationToken)
    {
        return await db.ReadProgress
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.BookId == bookId && p.TokenId == tokenId, cancellationToken);
    }

    /// <summary>
    /// Upserts the position for (book, token). Returns null when the book does not exist,
    /// so the caller can answer 404 without ever writing a row for an unknown book.
    /// </summary>
    public async Task<ReadProgressEntity?> UpsertAsync(
        Guid bookId,
        Guid tokenId,
        string progress,
        double? percentage,
        string? device,
        string? deviceId,
        CancellationToken cancellationToken)
    {
        var bookExists = await db.Books.AnyAsync(b => b.Id == bookId, cancellationToken);

        if (!bookExists)
        {
            return null;
        }

        var row = await db.ReadProgress
            .SingleOrDefaultAsync(p => p.BookId == bookId && p.TokenId == tokenId, cancellationToken);

        if (row is null)
        {
            row = new ReadProgressEntity
            {
                BookId = bookId,
                TokenId = tokenId,
            };
            db.ReadProgress.Add(row);
        }

        row.Progress = progress;
        row.Percentage = percentage;
        row.Device = device;
        row.DeviceId = deviceId;
        row.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        return row;
    }
}
