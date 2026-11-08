using Bindery.Core;
using Bindery.Host.Data;
using Microsoft.EntityFrameworkCore;

namespace Bindery.Host.Library;

/// <summary>The row a sidecar was written onto, and whether it was created by this call.</summary>
public sealed record IndexedBook(BookEntity Book, bool WasNew);

/// <summary>
/// Writing a book's sidecar metadata into the index.
/// </summary>
/// <remarks>
/// Three paths file a book — a plugin download, an upload, and a rescan — and all three
/// have to produce the same rows, or "delete the database and rescan" stops being a
/// recovery path. That agreement is this class: they differ in how they get the files onto
/// disk and agree on what happens afterwards.
/// </remarks>
public sealed class BookIndexer(BinderyDbContext db, LibraryStore library, ILogger<BookIndexer> logger)
{
    /// <summary>The row a sidecar belongs to, matched by its own id or by where it sits.</summary>
    public Task<BookEntity?> FindForAsync(Guid id, string directoryPath, CancellationToken cancellationToken) =>
        db.Books
            .Include(book => book.Authors)
            .Include(book => book.Tags)
            .Include(book => book.Files)
            .FirstOrDefaultAsync(book => book.Id == id || book.DirectoryPath == directoryPath, cancellationToken);

    /// <summary>
    /// Indexes one book directory from the sidecar that describes it.
    /// </summary>
    /// <remarks>
    /// <paramref name="metadata"/> must already carry the placed files and cover path —
    /// what <see cref="LibraryStore.PlaceAsync"/> wrote, or what the sidecar on disk says.
    /// A file the sidecar lists but the volume does not have is logged and skipped: indexing
    /// what exists beats serving a zero-byte download.
    /// </remarks>
    public async Task<IndexedBook> IndexAsync(
        SidecarMetadata metadata,
        string directoryPath,
        BookEntity? existing,
        CancellationToken cancellationToken)
    {
        var book = existing;
        var isNew = book is null;

        if (book is null)
        {
            book = new BookEntity { Id = metadata.Id };
            db.Books.Add(book);
        }
        else
        {
            await LoadCollectionsAsync(book, cancellationToken);

            db.BookAuthors.RemoveRange(book.Authors);
            db.BookTags.RemoveRange(book.Tags);
            db.BookFiles.RemoveRange(book.Files);
            book.Authors.Clear();
            book.Tags.Clear();
            book.Files.Clear();
        }

        book.Title = metadata.Title;
        book.AddedAt = metadata.AddedAt;
        book.SortTitle = Domain.Naming.sortTitle(metadata.Title);
        book.Summary = metadata.Summary;
        book.Language = metadata.Language;
        book.Published = metadata.Published;
        book.UpdatedAt = metadata.UpdatedAt;
        book.SeriesIndex = metadata.SeriesIndex;
        book.SourceUrl = metadata.SourceUrl;
        book.SourcePlugin = metadata.SourcePlugin;
        book.SourceId = metadata.SourceId;
        book.Chapters = metadata.Chapters;
        book.DirectoryPath = directoryPath;
        book.CoverPath = metadata.CoverPath is not null && library.Exists(metadata.CoverPath)
            ? metadata.CoverPath
            : null;
        book.Series = metadata.Series is null ? null : await GetOrAddSeriesAsync(metadata.Series, cancellationToken);

        var order = 0;

        foreach (var name in metadata.Authors)
        {
            var author = await GetOrAddAuthorAsync(name, cancellationToken);
            book.Authors.Add(new BookAuthorEntity { Book = book, Author = author, Order = order++ });
        }

        foreach (var name in metadata.Tags.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var tag = await GetOrAddTagAsync(name, cancellationToken);
            book.Tags.Add(new BookTagEntity { Book = book, Tag = tag });
        }

        foreach (var file in metadata.Files)
        {
            var info = library.FileInfoFor(file.RelativePath);

            if (info is null)
            {
                logger.LogWarning(
                    "{Directory}: {Path} is listed in the sidecar but missing", directoryPath, file.RelativePath);
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
        return new IndexedBook(book, isNew);
    }

    /// <summary>Drops authors, tags, and series that no longer hold a book.</summary>
    public async Task PruneEmptyGroupingsAsync(CancellationToken cancellationToken)
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

    private async Task LoadCollectionsAsync(BookEntity book, CancellationToken cancellationToken)
    {
        var entry = db.Entry(book);

        await entry.Collection(candidate => candidate.Authors).LoadAsync(cancellationToken);
        await entry.Collection(candidate => candidate.Tags).LoadAsync(cancellationToken);
        await entry.Collection(candidate => candidate.Files).LoadAsync(cancellationToken);
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
