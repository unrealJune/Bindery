using System.IO.Compression;
using Bindery.Core;

namespace Bindery.Host.Library;

/// <summary>What an EPUB says about itself, plus where its cover lives inside the zip.</summary>
public sealed record EpubInspection(Epub.EpubMetadata Metadata, string? CoverEntry);

/// <summary>
/// The zip half of reading an EPUB.
/// </summary>
/// <remarks>
/// Deliberately thin: this opens the container, hands the two XML documents to
/// <see cref="Epub"/> in <c>Bindery.Core</c>, and resolves entry names. No new dependency —
/// an EPUB is a zip holding XML, and <c>System.IO.Compression</c> plus the core parser is
/// the whole job.
/// </remarks>
public static class EpubReader
{
    private const string ContainerEntry = "META-INF/container.xml";

    /// <summary>Metadata for an EPUB on disk, or <c>null</c> if it is not a readable one.</summary>
    public static EpubInspection? Inspect(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);

            var opfEntry = FindPackage(archive);

            if (opfEntry is null)
            {
                return null;
            }

            var metadata = Epub.parsePackage(ReadText(opfEntry));
            var coverHref = metadata.CoverHref.OrNull();

            var cover = coverHref is null
                ? null
                : Resolve(DirectoryOf(opfEntry.FullName), coverHref) is { } candidate
                  && archive.GetEntry(candidate) is not null
                    ? candidate
                    : null;

            return new EpubInspection(metadata, cover);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the cover named by <paramref name="coverEntry"/> into
    /// <paramref name="destinationDirectory"/>, returning the file it wrote.
    /// </summary>
    public static string? ExtractCover(string path, string coverEntry, string destinationDirectory)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var entry = archive.GetEntry(coverEntry);

            if (entry is null || entry.Length == 0)
            {
                return null;
            }

            var extension = Path.GetExtension(coverEntry).ToLowerInvariant();

            if (Domain.Formats.imageContentType(extension) == "application/octet-stream")
            {
                return null;
            }

            Directory.CreateDirectory(destinationDirectory);
            var target = Path.Combine(destinationDirectory, "cover" + extension);

            using (var source = entry.Open())
            using (var file = File.Create(target))
            {
                source.CopyTo(file);
            }

            return target;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    private static ZipArchiveEntry? FindPackage(ZipArchive archive)
    {
        var container = archive.GetEntry(ContainerEntry);

        if (container is not null && Epub.rootfilePath(ReadText(container)).OrNull() is { } declared)
        {
            var entry = archive.GetEntry(declared.Replace('\\', '/'));

            if (entry is not null)
            {
                return entry;
            }
        }

        // A container that lies, or is missing entirely, is common enough in
        // hand-assembled files to be worth one fallback rather than a rejection.
        return archive.Entries.FirstOrDefault(entry =>
            entry.FullName.EndsWith(".opf", StringComparison.OrdinalIgnoreCase));
    }

    private static string ReadText(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string DirectoryOf(string entryName)
    {
        var slash = entryName.LastIndexOf('/');
        return slash < 0 ? string.Empty : entryName[..slash];
    }

    /// <summary>Joins an OPF-relative href onto its directory, resolving <c>..</c> segments.</summary>
    private static string? Resolve(string directory, string href)
    {
        var trimmed = href.Split('#')[0];

        if (trimmed.Length == 0)
        {
            return null;
        }

        // Hrefs inside an OPF are URI references, so a space really is "%20" in the zip's
        // eyes only after decoding.
        trimmed = Uri.UnescapeDataString(trimmed).Replace('\\', '/');

        var segments = new List<string>();

        if (!trimmed.StartsWith('/') && directory.Length > 0)
        {
            segments.AddRange(directory.Split('/', StringSplitOptions.RemoveEmptyEntries));
        }

        foreach (var segment in trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    return null;
                }

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        return segments.Count == 0 ? null : string.Join('/', segments);
    }
}
