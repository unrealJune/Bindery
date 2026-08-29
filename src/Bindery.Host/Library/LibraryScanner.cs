using Bindery.Host.Data;
using Microsoft.EntityFrameworkCore;

namespace Bindery.Host.Library;

public sealed record ScanReport(int Added, int Updated, int Removed, int Skipped, IReadOnlyList<string> Problems)
{
    public int Total => Added + Updated;
}

/// <summary>
/// Rebuilds the index from the files on disk.
/// </summary>
/// <remarks>
/// This is what makes "the database is a rebuildable index" a fact rather than a slogan.
/// Delete <c>bindery.db</c>, restart, run a scan, and the library is back — because every
/// book directory carries a <c>bindery.json</c> sidecar with the metadata the database
/// would otherwise be the only copy of.
/// </remarks>
public sealed class LibraryScanner(
    BinderyDbContext db,
    LibraryStore library,
    BookIndexer indexer,
    ILogger<LibraryScanner> logger)
{
    public async Task<ScanReport> ScanAsync(CancellationToken cancellationToken)
    {
        var added = 0;
        var updated = 0;
        var skipped = 0;
        var problems = new List<string>();

        var onDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in library.EnumerateBookDirectories())
        {
            cancellationToken.ThrowIfCancellationRequested();
            onDisk.Add(directory);

            var sidecar = await library.ReadSidecarAsync(directory, cancellationToken);

            if (sidecar is null)
            {
                problems.Add($"{directory}: unreadable {LibraryStore.SidecarName}");
                skipped++;
                continue;
            }

            try
            {
                var existing = await indexer.FindForAsync(sidecar.Id, directory, cancellationToken);
                var indexed = await indexer.IndexAsync(sidecar, directory, existing, cancellationToken);

                if (indexed.WasNew)
                {
                    added++;
                }
                else
                {
                    updated++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "could not index {Directory}", directory);
                problems.Add($"{directory}: {ex.Message}");
                skipped++;
            }
        }

        // Rows whose directory is gone. The files are the source of truth, so the row goes
        // — never the other way round.
        var orphans = await db.Books
            .Where(book => !string.IsNullOrEmpty(book.DirectoryPath))
            .Select(book => new { book.Id, book.DirectoryPath })
            .ToListAsync(cancellationToken);

        var removedIds = orphans
            .Where(candidate => !onDisk.Contains(candidate.DirectoryPath))
            .Select(candidate => candidate.Id)
            .ToList();

        if (removedIds.Count > 0)
        {
            var rows = await db.Books.Where(book => removedIds.Contains(book.Id)).ToListAsync(cancellationToken);
            db.Books.RemoveRange(rows);
            await db.SaveChangesAsync(cancellationToken);
        }

        await indexer.PruneEmptyGroupingsAsync(cancellationToken);

        var report = new ScanReport(added, updated, removedIds.Count, skipped, problems);

        logger.LogInformation(
            "library scan complete: {Added} added, {Updated} updated, {Removed} removed, {Skipped} skipped",
            report.Added, report.Updated, report.Removed, report.Skipped);

        return report;
    }
}
