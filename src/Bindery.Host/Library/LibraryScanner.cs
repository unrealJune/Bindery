using Bindery.Core;
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
                if (await ApplyAsync(directory, sidecar, cancellationToken))
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

        await PruneEmptyGroupingsAsync(cancellationToken);

        var report = new ScanReport(added, updated, removedIds.Count, skipped, problems);

        logger.LogInformation(
            "library scan complete: {Added} added, {Updated} updated, {Removed} removed, {Skipped} skipped",
            report.Added, report.Updated, report.Removed, report.Skipped);

        return report;
    }

    /// <summary>Indexes one directory. Returns true when the book was new.</summary>
    private async Task<bool> ApplyAsync(string directory, SidecarMetadata sidecar, CancellationToken cancellationToken)
    {
        var book = await db.Books
            .Include(candidate => candidate.Authors)
            .Include(candidate => candidate.Tags)
            .Include(candidate => candidate.Files)
            .FirstOrDefaultAsync(candidate => candidate.Id == sidecar.Id || candidate.DirectoryPath == directory,
                cancellationToken);

        var isNew = book is null;

        if (book is null)
        {
            book = new BookEntity { Id = sidecar.Id };
            db.Books.Add(book);
        }
        else
        {
            db.BookAuthors.RemoveRange(book.Authors);
            db.BookTags.RemoveRange(book.Tags);
            db.BookFiles.RemoveRange(book.Files);
            book.Authors.Clear();
            book.Tags.Clear();
            book.Files.Clear();
        }

        book.Title = sidecar.Title;
        book.SortTitle = Domain.Naming.sortTitle(sidecar.Title);
        book.Summary = sidecar.Summary;
        book.Language = sidecar.Language;
        book.Published = sidecar.Published;
        book.AddedAt = sidecar.AddedAt;
        book.UpdatedAt = sidecar.UpdatedAt;
        book.SeriesIndex = sidecar.SeriesIndex;
        book.SourceUrl = sidecar.SourceUrl;
        book.SourcePlugin = sidecar.SourcePlugin;
        book.SourceId = sidecar.SourceId;
        book.Chapters = sidecar.Chapters;
        book.DirectoryPath = directory;
        book.CoverPath = sidecar.CoverPath is not null && library.Exists(sidecar.CoverPath) ? sidecar.CoverPath : null;
        book.Series = sidecar.Series is null ? null : await GetOrAddSeriesAsync(sidecar.Series, cancellationToken);

        var order = 0;

        foreach (var name in sidecar.Authors)
        {
            var author = await GetOrAddAuthorAsync(name, cancellationToken);
            book.Authors.Add(new BookAuthorEntity { Book = book, Author = author, Order = order++ });
        }

        foreach (var name in sidecar.Tags.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var tag = await GetOrAddTagAsync(name, cancellationToken);
            book.Tags.Add(new BookTagEntity { Book = book, Tag = tag });
        }

        foreach (var file in sidecar.Files)
        {
            var info = library.FileInfoFor(file.RelativePath);

            if (info is null)
            {
                // The sidecar lists a file that is not there. Index what exists; a missing
                // file is better reported than served as a zero-byte download.
                logger.LogWarning("{Directory}: {Path} is listed in the sidecar but missing", directory, file.RelativePath);
                continue;
            }

            book.Files.Add(new BookFileEntity
            {
                Book = book,
                Format = file.Format,
                ContentType = string.IsNullOrEmpty(file.ContentType)
                    ? Domain.Formats.contentType(file.Format)
                    : file.ContentType,
                RelativePath = file.RelativePath,
                SizeBytes = info.Length,
                Sha256 = file.Sha256
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        return isNew;
    }

    private async Task PruneEmptyGroupingsAsync(CancellationToken cancellationToken)
    {
        var authors = await db.Authors.Where(author => author.Books.Count == 0).ToListAsync(cancellationToken);
        var tags = await db.Tags.Where(tag => tag.Books.Count == 0).ToListAsync(cancellationToken);
        var series = await db.Series.Where(entry => entry.Books.Count == 0).ToListAsync(cancellationToken);

        db.Authors.RemoveRange(authors);
        db.Tags.RemoveRange(tags);
        db.Series.RemoveRange(series);

        if (authors.Count + tags.Count + series.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<AuthorEntity> GetOrAddAuthorAsync(string name, CancellationToken cancellationToken) =>
        await db.Authors.FirstOrDefaultAsync(author => author.Name == name, cancellationToken)
        ?? AddAndReturn(new AuthorEntity { Name = name, SortName = Domain.Naming.sortAuthor(name) });

    private async Task<TagEntity> GetOrAddTagAsync(string name, CancellationToken cancellationToken) =>
        await db.Tags.FirstOrDefaultAsync(tag => tag.Name == name, cancellationToken)
        ?? AddAndReturn(new TagEntity { Name = name });

    private async Task<SeriesEntity> GetOrAddSeriesAsync(string name, CancellationToken cancellationToken) =>
        await db.Series.FirstOrDefaultAsync(series => series.Name == name, cancellationToken)
        ?? AddAndReturn(new SeriesEntity { Name = name });

    private T AddAndReturn<T>(T entity) where T : class
    {
        db.Add(entity);
        return entity;
    }
}
