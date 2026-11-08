using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bindery.Core;
using Bindery.Host.Configuration;
using Microsoft.Extensions.Options;

namespace Bindery.Host.Library;

/// <summary>A file that has been fetched from a plugin and is waiting to be filed.</summary>
public sealed record StagedFile(
    string TempPath,
    string Format,
    string ContentType,
    string SuggestedName,
    long SizeBytes,
    string Sha256,
    bool IsCover);

/// <summary>Where a book ended up on disk.</summary>
public sealed record Placement(
    string DirectoryPath,
    IReadOnlyList<PlacedFile> Files,
    string? CoverPath);

public sealed record PlacedFile(string Format, string ContentType, string RelativePath, long SizeBytes, string Sha256);

/// <summary>
/// The library on disk.
/// </summary>
/// <remarks>
/// Layout is <c>{Author}/{Title}/{file}</c>, human-browsable and rsync-able on purpose.
/// Every book directory also gets a <c>bindery.json</c> sidecar holding the metadata the
/// database would otherwise be the only copy of — which is what makes "delete the database
/// and rescan" a real recovery path rather than an aspiration.
/// </remarks>
public sealed class LibraryStore(IOptions<BinderyOptions> options, ILogger<LibraryStore> logger)
{
    public const string SidecarName = "bindery.json";

    private static readonly JsonSerializerOptions SidecarJson = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly BinderyOptions _options = options.Value;

    public string Root => _options.LibraryPath;

    public void EnsureRoot()
    {
        Directory.CreateDirectory(Root);
    }

    /// <summary>Absolute path for a library-relative path, refusing anything that escapes.</summary>
    public string Resolve(string relativePath)
    {
        var root = Path.GetFullPath(Root);
        var combined = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        // Relative paths come from the database, which is not user input — but a rescan
        // reads them off disk, and defence in depth costs one comparison.
        if (!combined.StartsWith(root, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"path escapes the library root: {relativePath}");
        }

        return combined;
    }

    public bool Exists(string relativePath) => File.Exists(Resolve(relativePath));

    /// <summary>
    /// Moves staged files into their final home and writes the sidecar.
    /// </summary>
    /// <remarks>
    /// Replacing an existing book is deliberate and destructive-by-necessity: an updated
    /// story is the same book with more chapters, and keeping both would double the
    /// library on every refresh. Files are replaced in place so the directory is never
    /// empty between the two.
    /// </remarks>
    public async Task<Placement> PlaceAsync(
        SidecarMetadata metadata,
        IReadOnlyList<StagedFile> staged,
        string? existingDirectory,
        CancellationToken cancellationToken)
    {
        EnsureRoot();

        var authorLine = metadata.Authors.Count > 0 ? string.Join(", ", metadata.Authors) : "Unknown";
        var directory = existingDirectory ?? Domain.Naming.bookDirectory(authorLine, metadata.Title);
        directory = await EnsureUniqueDirectoryAsync(directory, metadata, cancellationToken);

        var absolute = Resolve(directory);
        Directory.CreateDirectory(absolute);

        var placed = new List<PlacedFile>();
        string? coverPath = null;

        foreach (var file in staged)
        {
            var name = file.IsCover
                ? "cover" + Path.GetExtension(file.SuggestedName).ToLowerInvariant()
                : Domain.Naming.fileName(metadata.Title, authorLine, file.Format);

            var relative = $"{directory}/{name}";
            var target = Resolve(relative);

            File.Move(file.TempPath, target, overwrite: true);

            if (file.IsCover)
            {
                coverPath = relative;
            }
            else
            {
                placed.Add(new PlacedFile(file.Format, file.ContentType, relative, file.SizeBytes, file.Sha256));
            }
        }

        await WriteSidecarAsync(directory, metadata with { Files = [.. placed], CoverPath = coverPath }, cancellationToken);

        logger.LogInformation("filed {Title} into {Directory} ({Count} file(s))", metadata.Title, directory, placed.Count);
        return new Placement(directory, placed, coverPath);
    }

    /// <summary>
    /// Picks a directory that is not already someone else's book.
    /// </summary>
    /// <remarks>
    /// Two different works can share an author and a title — fanfic especially — so the
    /// sidecar's source id decides whether an occupied directory is the same book being
    /// refreshed or a genuine collision needing a suffix.
    /// </remarks>
    private async Task<string> EnsureUniqueDirectoryAsync(
        string preferred,
        SidecarMetadata metadata,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var candidate = attempt == 0 ? preferred : $"{preferred} ({attempt + 1})";
            var absolute = Resolve(candidate);

            if (!Directory.Exists(absolute))
            {
                return candidate;
            }

            var existing = await ReadSidecarAsync(candidate, cancellationToken);

            if (existing is null || SameWork(existing, metadata))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"could not find a free directory for {preferred}");
    }

    private static bool SameWork(SidecarMetadata existing, SidecarMetadata incoming) =>
        (existing.SourceId is not null && existing.SourceId == incoming.SourceId)
        || (existing.SourceUrl is not null && existing.SourceUrl == incoming.SourceUrl);

    public async Task WriteSidecarAsync(string directory, SidecarMetadata metadata, CancellationToken cancellationToken)
    {
        var path = Resolve($"{directory}/{SidecarName}");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, metadata, SidecarJson, cancellationToken);
    }

    public async Task<SidecarMetadata?> ReadSidecarAsync(string directory, CancellationToken cancellationToken)
    {
        var path = Resolve($"{directory}/{SidecarName}");

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<SidecarMetadata>(stream, SidecarJson, cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            logger.LogWarning(ex, "unreadable sidecar at {Path}", path);
            return null;
        }
    }

    /// <summary>Every book directory on disk, found by its sidecar.</summary>
    public IEnumerable<string> EnumerateBookDirectories()
    {
        EnsureRoot();
        var root = Path.GetFullPath(Root);

        foreach (var sidecar in Directory.EnumerateFiles(root, SidecarName, SearchOption.AllDirectories))
        {
            var directory = Path.GetDirectoryName(sidecar)!;
            yield return Path.GetRelativePath(root, directory).Replace(Path.DirectorySeparatorChar, '/');
        }
    }

    public void DeleteBook(string directory)
    {
        var absolute = Resolve(directory);

        // Resolve("") is the library root, and a recursive delete of that is the whole
        // library. A row with no directory is a bug elsewhere; it must not become one here.
        if (absolute == Path.GetFullPath(Root))
        {
            throw new InvalidOperationException("refusing to delete the library root");
        }

        if (Directory.Exists(absolute))
        {
            Directory.Delete(absolute, recursive: true);
        }

        // Leave the author directory behind only if it still holds something.
        var parent = Directory.GetParent(absolute);

        if (parent is not null
            && parent.Exists
            && parent.FullName != Path.GetFullPath(Root)
            && !parent.EnumerateFileSystemInfos().Any())
        {
            parent.Delete();
        }
    }

    public FileInfo? FileInfoFor(string relativePath)
    {
        var info = new FileInfo(Resolve(relativePath));
        return info.Exists ? info : null;
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

/// <summary>
/// What is written to <c>bindery.json</c> beside a book.
/// </summary>
/// <remarks>
/// This is a stored format: adding members is fine, renaming or repurposing them is not.
/// It exists so the library survives the database.
/// </remarks>
public sealed record SidecarMetadata
{
    public int Version { get; init; } = 1;

    public Guid Id { get; init; } = Guid.NewGuid();

    public string Title { get; init; } = string.Empty;

    public IReadOnlyList<string> Authors { get; init; } = [];

    public string? Series { get; init; }

    public double? SeriesIndex { get; init; }

    public string? Summary { get; init; }

    public string? Language { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public int? Chapters { get; init; }

    public string? SourceUrl { get; init; }

    public string? SourcePlugin { get; init; }

    public string? SourceId { get; init; }

    public DateTimeOffset? Published { get; init; }

    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    public IReadOnlyList<PlacedFile> Files { get; init; } = [];

    public string? CoverPath { get; init; }
}
