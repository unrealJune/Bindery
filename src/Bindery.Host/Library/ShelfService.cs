using Bindery.Host.Data;
using Bindery.Host.Security;
using Microsoft.EntityFrameworkCore;

namespace Bindery.Host.Library;

/// <summary>
/// Whose books are whose.
/// </summary>
/// <remarks>
/// <para>
/// The files are shared: one copy on disk however many people want a book. A shelf is a
/// person's claim on it, and it is the only thing that decides whose catalog a book appears
/// in. Every catalog query filters through it; nothing reads the library by id alone.
/// </para>
/// <para>
/// The same ordering rule as everywhere else in the library applies: the sidecar is written
/// first and the index follows, so "delete the database and rescan" brings every shelf back.
/// </para>
/// </remarks>
public sealed class ShelfService(
    BinderyDbContext db,
    LibraryStore library,
    UserDirectory users,
    ILogger<ShelfService> logger)
{
    // Sidecar writes are read-modify-write; two people shelving one book at once must not
    // lose either of them. Process-wide, because the store is.
    private static readonly SemaphoreSlim SidecarGate = new(1, 1);

    /// <summary>
    /// Holds off every shelf change while a caller reads or rewrites sidecars itself — a
    /// download replacing a book, a rescan reading one — so neither loses the other's owner.
    /// </summary>
    public static async Task<IDisposable> HoldAsync(CancellationToken cancellationToken)
    {
        await SidecarGate.WaitAsync(cancellationToken);
        return new Release();
    }

    /// <summary>
    /// The owner list a sidecar being (re)written for <paramref name="bookId"/> should carry:
    /// whoever has it now, plus <paramref name="adding"/>. Call under <see cref="HoldAsync"/>.
    /// </summary>
    /// <remarks>
    /// Never empty. A book nobody asked for — an old job queued before shelves, with no
    /// requester — goes to the founder rather than to nobody, and a book with no founder to
    /// give it to is left for the first sign-in to adopt.
    /// </remarks>
    public async Task<IReadOnlyList<SidecarOwner>?> OwnersForPlacementAsync(
        Guid? bookId,
        string? adding,
        CancellationToken cancellationToken)
    {
        var owners = bookId is { } id
            ? await db.Shelves
                .AsNoTracking()
                .Where(entry => entry.BookId == id)
                .Select(entry => new SidecarOwner(entry.Owner, entry.AddedAt))
                .ToListAsync(cancellationToken)
            : [];

        var newcomer = adding ?? (owners.Count == 0 ? await users.FounderAsync(cancellationToken) : null);

        if (!string.IsNullOrEmpty(newcomer) && owners.All(owner => owner.Subject != newcomer))
        {
            owners.Add(new SidecarOwner(newcomer, DateTimeOffset.UtcNow));
        }

        return owners.Count == 0 ? null : [.. owners.OrderBy(owner => owner.AddedAt)];
    }

    /// <summary>Whether <paramref name="owner"/> has this book.</summary>
    public Task<bool> HasAsync(Guid bookId, string owner, CancellationToken cancellationToken) =>
        db.Shelves.AnyAsync(entry => entry.BookId == bookId && entry.Owner == owner, cancellationToken);

    /// <summary>Everyone who has this book, earliest first.</summary>
    public async Task<IReadOnlyList<string>> OwnersAsync(Guid bookId, CancellationToken cancellationToken) =>
        await db.Shelves
            .AsNoTracking()
            .Where(entry => entry.BookId == bookId)
            .OrderBy(entry => entry.AddedAt)
            .ThenBy(entry => entry.Owner)
            .Select(entry => entry.Owner)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Puts a book on someone's shelf. True if it was not there already; false also when
    /// there is no such book.
    /// </summary>
    public async Task<bool> AddAsync(Guid bookId, string owner, CancellationToken cancellationToken)
    {
        await SidecarGate.WaitAsync(cancellationToken);

        try
        {
            var book = await db.Books
                .Include(candidate => candidate.Shelf)
                .FirstOrDefaultAsync(candidate => candidate.Id == bookId, cancellationToken);

            if (book is null || book.Shelf.Any(entry => entry.Owner == owner))
            {
                return false;
            }

            var entry = new ShelfEntryEntity { BookId = book.Id, Owner = owner, AddedAt = DateTimeOffset.UtcNow };
            await WriteOwnersAsync(book, [.. book.Shelf, entry], cancellationToken);

            book.Shelf.Add(entry);
            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation("shelved {Book} for {Owner}", book.Id, owner);
            return true;
        }
        finally
        {
            SidecarGate.Release();
        }
    }

    /// <summary>
    /// Takes a book off someone's shelf. Returns how many owners it still has, or null when
    /// it was never on this person's shelf — which is also the answer for a book that does
    /// not exist, so the two cannot be told apart.
    /// </summary>
    /// <remarks>
    /// When the last owner leaves, <paramref name="lastOwnerLeft"/> is handed the book to
    /// delete — still under the gate, so nobody can shelve it while its files go. The files
    /// are <see cref="LibraryWriter"/>'s business, not this class's; the row's cascade takes
    /// the shelf entry with it.
    /// </remarks>
    public async Task<int?> RemoveAsync(
        Guid bookId,
        string owner,
        Func<BookEntity, Task> lastOwnerLeft,
        CancellationToken cancellationToken)
    {
        await SidecarGate.WaitAsync(cancellationToken);

        try
        {
            var book = await db.Books
                .Include(candidate => candidate.Shelf)
                .FirstOrDefaultAsync(candidate => candidate.Id == bookId, cancellationToken);

            var entry = book?.Shelf.FirstOrDefault(candidate => candidate.Owner == owner);

            if (book is null || entry is null)
            {
                return null;
            }

            var remaining = book.Shelf.Where(candidate => candidate != entry).ToList();

            if (remaining.Count == 0)
            {
                await lastOwnerLeft(book);
                logger.LogInformation("unshelved {Book} for {Owner}, its last owner", book.Id, owner);
                return 0;
            }

            await WriteOwnersAsync(book, remaining, cancellationToken);

            book.Shelf.Remove(entry);
            db.Shelves.Remove(entry);
            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation("unshelved {Book} for {Owner}; {Count} owner(s) remain", book.Id, owner, remaining.Count);
            return remaining.Count;
        }
        finally
        {
            SidecarGate.Release();
        }
    }

    /// <summary>
    /// Gives every book nobody owns to the founder.
    /// </summary>
    /// <remarks>
    /// Books from before shelves existed, and books that arrived on the volume by rsync
    /// rather than through Bindery, belong to nobody. The founder — the earliest person
    /// seen — is who they belonged to in practice. Before anyone has signed in there is no
    /// founder, and this waits for the first sign-in to call it again.
    /// </remarks>
    public async Task<int> AdoptOrphansAsync(CancellationToken cancellationToken)
    {
        var founder = await users.FounderAsync(cancellationToken);

        if (founder is null)
        {
            return 0;
        }

        var orphans = await db.Books
            .Where(book => !book.Shelf.Any())
            .Select(book => book.Id)
            .ToListAsync(cancellationToken);

        var adopted = 0;

        foreach (var id in orphans)
        {
            try
            {
                if (await AddAsync(id, founder, cancellationToken))
                {
                    adopted++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "could not adopt {Book}", id);
            }
        }

        if (adopted > 0)
        {
            logger.LogInformation("shelved {Count} unowned book(s) for the founder", adopted);
        }

        return adopted;
    }

    /// <summary>Rewrites the sidecar's owner list, leaving every other member as it was.</summary>
    private async Task WriteOwnersAsync(
        BookEntity book,
        IReadOnlyList<ShelfEntryEntity> owners,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(book.DirectoryPath))
        {
            return;
        }

        var sidecar = await library.ReadSidecarAsync(book.DirectoryPath, cancellationToken);

        if (sidecar is null)
        {
            // The index row has nothing on disk to agree with; a rescan will remove it.
            logger.LogWarning("{Directory} has no readable sidecar; shelf recorded in the index only", book.DirectoryPath);
            return;
        }

        await library.WriteSidecarAsync(
            book.DirectoryPath,
            sidecar with
            {
                Owners = [.. owners
                    .OrderBy(entry => entry.AddedAt)
                    .Select(entry => new SidecarOwner(entry.Owner, entry.AddedAt))]
            },
            cancellationToken);
    }

    private sealed class Release : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                SidecarGate.Release();
            }
        }
    }
}
