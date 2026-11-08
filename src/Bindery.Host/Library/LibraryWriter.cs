using Bindery.Core;
using Bindery.Host.Configuration;
using Bindery.Host.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bindery.Host.Library;

/// <summary>What became of an upload.</summary>
public sealed record ImportResult(Guid? BookId, string? Title, bool AlreadyHeld, string? Problem)
{
    public bool Accepted => BookId is not null;
}

/// <summary>
/// The two ways a book enters or leaves the library without a plugin: uploaded by hand, and
/// deleted by hand.
/// </summary>
/// <remarks>
/// Both obey the same rule the download path does — the files on disk are the source of
/// truth, so they move first and the index follows. On delete that ordering matters twice
/// over: a crash after the directory is gone leaves a row that the next rescan removes,
/// whereas the reverse leaves files no row points at and no rescan reunites.
/// </remarks>
public sealed class LibraryWriter(
    BinderyDbContext db,
    LibraryStore library,
    BookIndexer indexer,
    IOptions<BinderyOptions> options,
    ILogger<LibraryWriter> logger)
{
    private readonly UploadOptions _uploads = options.Value.Uploads;

    /// <summary>Removes a book's files and then its rows. False when there was no such book.</summary>
    public async Task<bool> DeleteAsync(Guid bookId, CancellationToken cancellationToken)
    {
        var book = await db.Books.FirstOrDefaultAsync(candidate => candidate.Id == bookId, cancellationToken);

        if (book is null)
        {
            return false;
        }

        var title = book.Title;

        if (!string.IsNullOrEmpty(book.DirectoryPath))
        {
            library.DeleteBook(book.DirectoryPath);
        }

        // Jobs keep their history: the foreign key is SET NULL precisely so that losing the
        // book does not lose the record that it was once acquired.
        db.Books.Remove(book);
        await db.SaveChangesAsync(cancellationToken);
        await indexer.PruneEmptyGroupingsAsync(cancellationToken);

        logger.LogInformation("deleted {Title} ({Book}) and its files", title, bookId);
        return true;
    }

    /// <summary>
    /// Files an uploaded ebook.
    /// </summary>
    /// <remarks>
    /// An EPUB is asked what it is: title, authors, series, tags, and cover come out of its
    /// own OPF, so the common case is a file and nothing else to type. The overrides win
    /// when supplied, and for a format Bindery cannot read metadata out of they are the
    /// only thing there is — falling back to the filename, which is at least what the
    /// person called it.
    /// </remarks>
    public async Task<ImportResult> ImportAsync(
        Stream content,
        string fileName,
        string? titleOverride,
        string? authorOverride,
        CancellationToken cancellationToken)
    {
        var format = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();

        if (!Domain.Formats.preferred.Contains(format))
        {
            var known = string.Join(", ", Domain.Formats.preferred);

            return new ImportResult(null, null, false, format.Length == 0
                ? $"That file has no extension, so Bindery cannot tell what it is. Expected one of: {known}."
                : $"Bindery does not file .{format} files. Try one of: {known}.");
        }

        var staging = Path.Combine(options.Value.DataPath, "staging", $"upload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);

        try
        {
            var stagedPath = Path.Combine(staging, "upload." + format);
            var size = await WriteWithLimitAsync(content, stagedPath, cancellationToken);

            if (size is null)
            {
                return new ImportResult(null, null, false,
                    $"That file is larger than the {_uploads.MaxBytes / 1024 / 1024} MB upload limit.");
            }

            if (size == 0)
            {
                return new ImportResult(null, null, false, "That file is empty.");
            }

            var sha = await LibraryStore.ComputeSha256Async(stagedPath, cancellationToken);

            var duplicate = await db.BookFiles
                .AsNoTracking()
                .Where(file => file.Sha256 == sha)
                .Select(file => new { file.BookId, file.Book.Title })
                .FirstOrDefaultAsync(cancellationToken);

            if (duplicate is not null)
            {
                return new ImportResult(duplicate.BookId, duplicate.Title, true, null);
            }

            var inspection = format == "epub" ? EpubReader.Inspect(stagedPath) : null;
            var sidecar = BuildSidecar(inspection?.Metadata, fileName, titleOverride, authorOverride);

            var staged = new List<StagedFile>
            {
                new(stagedPath, format, Domain.Formats.contentType(format), fileName, size.Value, sha, IsCover: false)
            };

            if (inspection?.CoverEntry is { } coverEntry
                && EpubReader.ExtractCover(stagedPath, coverEntry, staging) is { } coverPath)
            {
                var coverInfo = new FileInfo(coverPath);

                staged.Add(new StagedFile(
                    coverPath,
                    Path.GetExtension(coverPath).TrimStart('.'),
                    Domain.Formats.imageContentType(Path.GetExtension(coverPath)),
                    Path.GetFileName(coverPath),
                    coverInfo.Length,
                    await LibraryStore.ComputeSha256Async(coverPath, cancellationToken),
                    IsCover: true));
            }

            var placement = await library.PlaceAsync(sidecar, staged, existingDirectory: null, cancellationToken);

            var placed = sidecar with { Files = placement.Files, CoverPath = placement.CoverPath };
            var existing = await indexer.FindForAsync(placed.Id, placement.DirectoryPath, cancellationToken);
            var indexed = await indexer.IndexAsync(placed, placement.DirectoryPath, existing, cancellationToken);

            logger.LogInformation("uploaded {Title} filed as {Directory}", placed.Title, placement.DirectoryPath);
            return new ImportResult(indexed.Book.Id, placed.Title, false, null);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    private SidecarMetadata BuildSidecar(
        Epub.EpubMetadata? metadata,
        string fileName,
        string? titleOverride,
        string? authorOverride)
    {
        var now = DateTimeOffset.UtcNow;

        var title = Coalesce(titleOverride, metadata?.Title.OrNull())
            ?? Path.GetFileNameWithoutExtension(fileName).Trim();

        IReadOnlyList<string> fromFile = metadata is null ? [] : metadata.Authors.AsList();

        IReadOnlyList<string> authors = Coalesce(authorOverride) is { } stated
            ? stated.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : fromFile;

        return new SidecarMetadata
        {
            Title = string.IsNullOrEmpty(title) ? "Untitled" : title,
            Authors = authors,
            Series = metadata?.Series.OrNull(),
            SeriesIndex = metadata?.SeriesIndex.OrNullable(),
            Summary = metadata?.Summary.OrNull(),
            Language = metadata?.Language.OrNull(),
            Tags = metadata is null ? [] : metadata.Tags.AsList(),
            Published = metadata?.Published.OrNullable(),
            // No SourcePlugin and no SourceUrl: nothing acquired this, so there is nothing to
            // refresh it from, and the register shows it as filed by hand.
            SourceId = metadata?.Identifier.OrNull(),
            AddedAt = now,
            UpdatedAt = now
        };
    }

    /// <summary>Copies up to the configured ceiling, returning null if the stream exceeds it.</summary>
    private async Task<long?> WriteWithLimitAsync(Stream content, string path, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;

        await using var file = File.Create(path);

        while (true)
        {
            var read = await content.ReadAsync(buffer, cancellationToken);

            if (read == 0)
            {
                return total;
            }

            total += read;

            if (total > _uploads.MaxBytes)
            {
                return null;
            }

            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static string? Coalesce(params string?[] candidates) =>
        candidates.Select(candidate => candidate?.Trim()).FirstOrDefault(candidate => !string.IsNullOrEmpty(candidate));

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "could not clear the upload staging directory {Path}", path);
        }
    }
}
