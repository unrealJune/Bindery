using System.ComponentModel.DataAnnotations;

namespace Bindery.Host.Data;

/// <summary>
/// A book in the library.
/// </summary>
/// <remarks>
/// The row is an index entry, not the book. The files on disk are the book, and
/// <see cref="DirectoryPath"/> plus the sidecar metadata written beside them is enough to
/// rebuild every row here. Never write a migration that breaks that.
/// </remarks>
public class BookEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(500)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(500)]
    public string SortTitle { get; set; } = string.Empty;

    public string? Summary { get; set; }

    [MaxLength(32)]
    public string? Language { get; set; }

    public DateTimeOffset? Published { get; set; }

    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public int? SeriesId { get; set; }

    public SeriesEntity? Series { get; set; }

    public double? SeriesIndex { get; set; }

    [MaxLength(2000)]
    public string? SourceUrl { get; set; }

    [MaxLength(64)]
    public string? SourcePlugin { get; set; }

    /// <summary>The plugin's stable id for the work, e.g. "ao3:12345". Drives updates.</summary>
    [MaxLength(200)]
    public string? SourceId { get; set; }

    public int? Chapters { get; set; }

    /// <summary>Relative to the library root, forward slashes, e.g. "Some Author/A Title".</summary>
    [MaxLength(500)]
    public string DirectoryPath { get; set; } = string.Empty;

    /// <summary>Relative path of the cover image, if there is one.</summary>
    [MaxLength(600)]
    public string? CoverPath { get; set; }

    public List<BookAuthorEntity> Authors { get; set; } = new();

    public List<BookTagEntity> Tags { get; set; } = new();

    public List<BookFileEntity> Files { get; set; } = new();
}

public class AuthorEntity
{
    public int Id { get; set; }

    [MaxLength(300)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(300)]
    public string SortName { get; set; } = string.Empty;

    public List<BookAuthorEntity> Books { get; set; } = new();
}

public class BookAuthorEntity
{
    public Guid BookId { get; set; }

    public BookEntity Book { get; set; } = null!;

    public int AuthorId { get; set; }

    public AuthorEntity Author { get; set; } = null!;

    /// <summary>Preserves the plugin's ordering, which is meaningful for co-authored works.</summary>
    public int Order { get; set; }
}

public class TagEntity
{
    public int Id { get; set; }

    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    public List<BookTagEntity> Books { get; set; } = new();
}

public class BookTagEntity
{
    public Guid BookId { get; set; }

    public BookEntity Book { get; set; } = null!;

    public int TagId { get; set; }

    public TagEntity Tag { get; set; } = null!;
}

public class SeriesEntity
{
    public int Id { get; set; }

    [MaxLength(300)]
    public string Name { get; set; } = string.Empty;

    public List<BookEntity> Books { get; set; } = new();
}

public class BookFileEntity
{
    public int Id { get; set; }

    public Guid BookId { get; set; }

    public BookEntity Book { get; set; } = null!;

    [MaxLength(16)]
    public string Format { get; set; } = string.Empty;

    [MaxLength(200)]
    public string ContentType { get; set; } = string.Empty;

    /// <summary>Relative to the library root, forward slashes.</summary>
    [MaxLength(700)]
    public string RelativePath { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    [MaxLength(64)]
    public string Sha256 { get; set; } = string.Empty;
}

public enum JobStatus
{
    Queued,
    Running,
    Succeeded,
    /// <summary>The plugin found nothing new. A success, not a failure.</summary>
    Unchanged,
    Failed,
    Cancelled
}

public class DownloadJobEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(2000)]
    public string Url { get; set; } = string.Empty;

    [MaxLength(64)]
    public string? PluginName { get; set; }

    public JobStatus Status { get; set; } = JobStatus.Queued;

    public double Percent { get; set; }

    [MaxLength(500)]
    public string? Message { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public int Attempts { get; set; }

    /// <summary>When a retryable failure should next be tried.</summary>
    public DateTimeOffset? NextAttemptAt { get; set; }

    [MaxLength(64)]
    public string? ErrorCode { get; set; }

    [MaxLength(2000)]
    public string? ErrorMessage { get; set; }

    public bool Retryable { get; set; }

    public Guid? BookId { get; set; }

    public BookEntity? Book { get; set; }

    /// <summary>Whether this was queued as a refresh of an existing book.</summary>
    public bool IsUpdate { get; set; }

    [MaxLength(200)]
    public string? RequestedBy { get; set; }

    public List<JobLogEntity> Log { get; set; } = new();
}

public class JobLogEntity
{
    public long Id { get; set; }

    public Guid JobId { get; set; }

    public DownloadJobEntity Job { get; set; } = null!;

    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;

    [MaxLength(16)]
    public string Level { get; set; } = "info";

    [MaxLength(2000)]
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// One configuration value for one plugin. Secret values are stored encrypted by ASP.NET
/// Core Data Protection and are never rendered back into HTML.
/// </summary>
public class PluginSettingEntity
{
    public int Id { get; set; }

    [MaxLength(64)]
    public string Plugin { get; set; } = string.Empty;

    [MaxLength(64)]
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public bool IsSecret { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A long-lived credential for an ereader. Stored hashed; the plaintext is shown once, at
/// creation, and never again.
/// </summary>
public class FeedTokenEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Base64 SHA-256 of the token. Compared in constant time.</summary>
    [MaxLength(64)]
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>First characters of the token, so a device can be identified in a list.</summary>
    [MaxLength(16)]
    public string Prefix { get; set; } = string.Empty;

    [MaxLength(200)]
    public string Subject { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastUsedAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public bool IsActive => RevokedAt is null;
}
