using Bindery.Core;
using Bindery.Host.Configuration;
using Bindery.Host.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bindery.Host.Library;

/// <summary>Whose files and description survive a merge.</summary>
public enum MergeKeep
{
    /// <summary>The book being merged into keeps its own files and metadata.</summary>
    Target,

    /// <summary>The other book's files and metadata replace the target's.</summary>
    Other
}

/// <summary>What became of a merge.</summary>
public sealed record MergeResult(bool Merged, string? Problem, string? Title = null);

/// <summary>What became of an upload.</summary>
public sealed record ImportResult(Guid? BookId, string? Title, bool AlreadyHeld, string? Problem)
{
    public bool Accepted => BookId is not null;
}

/// <summary>
/// The two ways a book enters or leaves a shelf without a plugin: uploaded by hand, and
/// deleted by hand.
/// </summary>
/// <remarks>
/// Both obey the same rule the download path does — the files on disk are the source of
/// truth, so they move first and the index follows. On delete that ordering matters twice
/// over: a crash after the directory is gone leaves a row that the next rescan removes,
/// whereas the reverse leaves files no row points at and no rescan reunites.
/// <para>
/// The files are shared and the shelves are not. Uploading a file someone else already
/// filed shelves their copy; deleting takes the book off your shelf, and only the last
/// owner leaving deletes the files. Neither says so: what is on other people's shelves is
/// not this person's business.
/// </para>
/// </remarks>
public sealed class LibraryWriter(
    BinderyDbContext db,
    LibraryStore library,
    BookIndexer indexer,
    ShelfService shelves,
    IOptions<BinderyOptions> options,
    ILogger<LibraryWriter> logger)
{
    private readonly UploadOptions _uploads = options.Value.Uploads;

    /// <summary>
    /// Takes a book off <paramref name="owner"/>'s shelf, removing its files and rows when
    /// nobody else has it. False when it was not on their shelf.
    /// </summary>
    public async Task<bool> DeleteAsync(Guid bookId, string owner, CancellationToken cancellationToken)
    {
        var remaining = await shelves.RemoveAsync(bookId, owner, async book =>
        {
            if (!string.IsNullOrEmpty(book.DirectoryPath))
            {
                library.DeleteBook(book.DirectoryPath);
            }

            // Jobs keep their history: the foreign key is SET NULL precisely so that losing
            // the book does not lose the record that it was once acquired.
            db.Books.Remove(book);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("deleted {Title} ({Book}) and its files", book.Title, book.Id);
        }, cancellationToken);

        if (remaining is null)
        {
            return false;
        }

        if (remaining == 0)
        {
            await indexer.PruneEmptyGroupingsAsync(cancellationToken);
        }

        return true;
    }

    /// <summary>
    /// Folds one book into another: the two become one book with every source either had.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both books must be on <paramref name="owner"/>'s shelf. The target keeps its identity
    /// — its id, its directory, its place in OPDS feeds, read progress — so ereaders that
    /// know it keep working. <paramref name="keep"/> says whose <em>files and description</em>
    /// it ends up with; the followed source stays the target's unless
    /// <paramref name="followOther"/> asks for the other book's.
    /// </para>
    /// <para>
    /// Books are shared, and what is on anyone else's shelf is theirs. So when somebody else
    /// also holds the other book, it is not deleted out from under them: only this person's
    /// claim on it is given up, after its sources have been folded into the target. Either
    /// way the person sees the same result, and learns nothing about anyone else's shelf.
    /// </para>
    /// <para>
    /// Files first, as everywhere here: the target's files and sidecar are written (the
    /// other book's files copied in, when they are the ones kept), then the other book goes,
    /// then the index follows. A crash part-way leaves at worst both books on disk, which a
    /// second merge finishes. Never a book with nothing behind it.
    /// </para>
    /// </remarks>
    public async Task<MergeResult> MergeAsync(
        string owner,
        Guid targetId,
        Guid otherId,
        MergeKeep keep,
        bool followOther,
        CancellationToken cancellationToken)
    {
        if (targetId == otherId)
        {
            return new MergeResult(false, "A book cannot be merged with itself.");
        }

        if (!await shelves.HasAsync(targetId, owner, cancellationToken)
            || !await shelves.HasAsync(otherId, owner, cancellationToken))
        {
            return new MergeResult(false, "One of those books is gone.");
        }

        var sharedWithOthers = (await shelves.OwnersAsync(otherId, cancellationToken)).Any(subject => subject != owner);
        SidecarMetadata merged;

        using (await ShelfService.HoldAsync(cancellationToken))
        {
            var target = await db.Books.Include(book => book.Sources)
                .FirstOrDefaultAsync(book => book.Id == targetId, cancellationToken);
            var other = await db.Books.Include(book => book.Sources)
                .FirstOrDefaultAsync(book => book.Id == otherId, cancellationToken);

            if (target is null || other is null)
            {
                return new MergeResult(false, "One of those books is gone.");
            }

            var targetSidecar = await library.ReadSidecarAsync(target.DirectoryPath, cancellationToken);
            var otherSidecar = await library.ReadSidecarAsync(other.DirectoryPath, cancellationToken);

            if (targetSidecar is null || otherSidecar is null)
            {
                return new MergeResult(false, "One of those books has no readable bindery.json; rescan the library first.");
            }

            var sources = SidecarSource.Fold(targetSidecar.AllSources().Concat(otherSidecar.AllSources()));
            var followed = followOther && otherSidecar.SourcePlugin is not null ? otherSidecar : targetSidecar;
            var directory = target.DirectoryPath;

            if (keep == MergeKeep.Target)
            {
                merged = targetSidecar with { Sources = sources };
            }
            else
            {
                var staging = Path.Combine(options.Value.DataPath, "staging", $"merge-{Guid.NewGuid():N}");
                Directory.CreateDirectory(staging);

                try
                {
                    // Copied, not moved: the other book must stay whole on disk until the
                    // target is written — and stays for good when someone else holds it.
                    var staged = new List<StagedFile>();

                    foreach (var file in otherSidecar.Files)
                    {
                        staged.Add(await StageCopyAsync(
                            file.RelativePath, file.Format, file.ContentType, file.Sha256, false, staging, cancellationToken));
                    }

                    if (otherSidecar.CoverPath is { } cover && library.Exists(cover))
                    {
                        staged.Add(await StageCopyAsync(cover, Path.GetExtension(cover).TrimStart('.'),
                            Domain.Formats.imageContentType(Path.GetExtension(cover)), null, true, staging, cancellationToken));
                    }

                    var description = otherSidecar with
                    {
                        Id = targetSidecar.Id,
                        AddedAt = targetSidecar.AddedAt < otherSidecar.AddedAt ? targetSidecar.AddedAt : otherSidecar.AddedAt,
                        Owners = targetSidecar.Owners,
                        Sources = sources
                    };

                    var placement = await library.PlaceAsync(description, staged, target.DirectoryPath, cancellationToken);
                    merged = description with { Files = placement.Files, CoverPath = placement.CoverPath };
                    directory = placement.DirectoryPath;
                }
                finally
                {
                    TryDeleteDirectory(staging);
                }
            }

            // The target's owners stay exactly the target's: the person merging already
            // holds it, and anyone holding only the other book keeps that book.
            merged = merged with
            {
                Owners = targetSidecar.Owners,
                SourcePlugin = followed.SourcePlugin,
                SourceUrl = followed.SourceUrl,
                SourceId = followed.SourceId
            };
            await library.WriteSidecarAsync(directory, merged, cancellationToken);

            if (!sharedWithOthers)
            {
                if (!string.IsNullOrEmpty(other.DirectoryPath))
                {
                    library.DeleteBook(other.DirectoryPath);
                }

                // History and reading position follow the book. Progress the target already
                // has for a device wins over the other book's; the rest moves across.
                await db.Jobs.Where(job => job.BookId == other.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.BookId, target.Id), cancellationToken);

                var targetTokens = await db.ReadProgress
                    .Where(progress => progress.BookId == target.Id)
                    .Select(progress => progress.TokenId)
                    .ToListAsync(cancellationToken);

                await db.ReadProgress
                    .Where(progress => progress.BookId == other.Id && !targetTokens.Contains(progress.TokenId))
                    .ExecuteUpdateAsync(setters => setters.SetProperty(progress => progress.BookId, target.Id), cancellationToken);

                db.Books.Remove(other);
                await db.SaveChangesAsync(cancellationToken);
            }

            await indexer.IndexAsync(merged, directory, target, cancellationToken);
        }

        if (sharedWithOthers)
        {
            // Outside the gate: giving up a shelf claim takes it itself.
            await DeleteAsync(otherId, owner, cancellationToken);
        }

        await indexer.PruneEmptyGroupingsAsync(cancellationToken);

        logger.LogInformation("merged {Other} into {Target} for {Owner}, keeping {Keep}'s files{Shared}",
            otherId, targetId, owner, keep, sharedWithOthers ? " (the other book stays for its other owners)" : "");
        return new MergeResult(true, null, merged.Title);
    }

    private async Task<StagedFile> StageCopyAsync(
        string relativePath,
        string format,
        string contentType,
        string? sha256,
        bool isCover,
        string staging,
        CancellationToken cancellationToken)
    {
        var source = library.Resolve(relativePath);
        var copy = Path.Combine(staging, Guid.NewGuid().ToString("N") + Path.GetExtension(relativePath));
        File.Copy(source, copy);

        return new StagedFile(
            copy,
            format,
            contentType,
            Path.GetFileName(relativePath),
            new FileInfo(copy).Length,
            sha256 ?? await LibraryStore.ComputeSha256Async(copy, cancellationToken),
            isCover);
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
        string owner,
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
                // "Already held" means on *your* shelf. Someone else's copy is shelved for
                // you without a word about whose it was.
                var added = await shelves.AddAsync(duplicate.BookId, owner, cancellationToken);
                return new ImportResult(duplicate.BookId, duplicate.Title, !added, null);
            }

            var inspection = format == "epub" ? EpubReader.Inspect(stagedPath) : null;
            var sidecar = BuildSidecar(inspection?.Metadata, fileName, titleOverride, authorOverride) with
            {
                Owners = [new SidecarOwner(owner, DateTimeOffset.UtcNow)]
            };

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
