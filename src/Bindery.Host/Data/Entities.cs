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

    /// <summary>
    /// The stable identity (OIDC subject) of whoever queued this, when a person did.
    /// <see cref="RequestedBy"/> is a display name; this is who gets told how it ended.
    /// </summary>
    [MaxLength(200)]
    public string? RequesterSubject { get; set; }

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

/// <summary>
/// A reader's position in one book, as last reported by one feed token. One row per
/// (book, token); a new push overwrites the old position rather than growing history.
/// </summary>
public class ReadProgressEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid BookId { get; set; }

    public BookEntity? Book { get; set; }

    /// <summary>The feed token that pushed this position; anonymous to other tokens.</summary>
    public Guid TokenId { get; set; }

    /// <summary>Opaque reader-specific position (page, xpointer, percent — reader's choice).</summary>
    [MaxLength(500)]
    public string Progress { get; set; } = string.Empty;

    /// <summary>0..1 fraction of the book, when the reader can compute one.</summary>
    public double? Percentage { get; set; }

    [MaxLength(200)]
    public string? Device { get; set; }

    /// <summary>The reader's own instance fingerprint, passed through from the client.</summary>
    [MaxLength(200)]
    public string? DeviceId { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One person's account at a library, connected through a circulation plugin (protocol 3.8).
/// </summary>
/// <remarks>
/// Owned by a person, not the installation: a household has several cards, and a person
/// can hold cards at two library systems. Every query over this table filters by
/// <see cref="Owner"/>. Plain field values (library, card number) are stored as JSON; the
/// secret ones (PIN) are a separate JSON blob encrypted with Data Protection, exactly as
/// plugin settings are, and are never rendered back.
/// </remarks>
public class CirculationAccountEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>OIDC subject of the person this account belongs to.</summary>
    [MaxLength(200)]
    public string Owner { get; set; } = string.Empty;

    [MaxLength(64)]
    public string Plugin { get; set; } = string.Empty;

    /// <summary>What the library calls itself, from the plugin's verify call.</summary>
    [MaxLength(200)]
    public string LibraryName { get; set; } = string.Empty;

    /// <summary>A non-secret description, e.g. "card ending 4821".</summary>
    [MaxLength(200)]
    public string? DisplayName { get; set; }

    /// <summary>Non-secret account field values, as a JSON object of strings.</summary>
    public string FieldsJson { get; set; } = "{}";

    /// <summary>Secret account field values, as a JSON object, Data-Protection encrypted.</summary>
    public string ProtectedSecrets { get; set; } = string.Empty;

    /// <summary>Pickup branches from the last verify, as a JSON array of {id, name}.</summary>
    public string PickupLocationsJson { get; set; } = "[]";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When a snapshot last reconciled successfully. Null until the first one.</summary>
    public DateTimeOffset? LastSyncedAt { get; set; }

    /// <summary>When a sync was last attempted, successful or not.</summary>
    public DateTimeOffset? LastAttemptAt { get; set; }

    [MaxLength(500)]
    public string? LastError { get; set; }

    /// <summary>
    /// The library refused the credentials. Syncing stops until they are re-entered, so a
    /// wrong PIN does not get a card locked by repeated attempts.
    /// </summary>
    public bool NeedsAttention { get; set; }

    public List<CirculationItemEntity> Items { get; set; } = new();
}

/// <summary>A hold or a loan on a circulation account, and where it is in its lifecycle.</summary>
/// <remarks>
/// A cache of the library's own records plus the conclusions Bindery drew from them
/// (docs/CIRCULATION.md §3). Deleting every row loses history and nothing else: the next sync
/// rebuilds every active hold and loan.
/// </remarks>
public class CirculationItemEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid AccountId { get; set; }

    public CirculationAccountEntity Account { get; set; } = null!;

    /// <summary>"hold" or "loan" — <c>Circulation.TrackedKind.Wire</c>.</summary>
    [MaxLength(8)]
    public string Kind { get; set; } = "hold";

    /// <summary>The library's id for this hold or loan.</summary>
    [MaxLength(200)]
    public string ExternalId { get; set; } = string.Empty;

    /// <summary>The catalog id; links a hold to the loan it became.</summary>
    [MaxLength(200)]
    public string? CatalogId { get; set; }

    /// <summary><c>Circulation.TrackedState.Wire</c>.</summary>
    [MaxLength(24)]
    public string State { get; set; } = "pending";

    [MaxLength(500)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Authors { get; set; }

    /// <summary>"physical" or "digital".</summary>
    [MaxLength(16)]
    public string Medium { get; set; } = "physical";

    [MaxLength(32)]
    public string? Format { get; set; }

    [MaxLength(2000)]
    public string? CoverUrl { get; set; }

    [MaxLength(2000)]
    public string? Url { get; set; }

    public int? QueuePosition { get; set; }

    public int? QueueSize { get; set; }

    [MaxLength(200)]
    public string? PickupLocation { get; set; }

    public DateTimeOffset? ReadyUntil { get; set; }

    public DateTimeOffset? SuspendedUntil { get; set; }

    public DateTimeOffset? DueAt { get; set; }

    public DateTimeOffset? PlacedAt { get; set; }

    /// <summary>The deadline a reminder was last sent for; see <c>Circulation.Tracked</c>.</summary>
    public DateTimeOffset? RemindedFor { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset StateChangedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<CirculationEventEntity> Events { get; set; } = new();
}

/// <summary>Something that happened to a hold or loan, as Bindery concluded it.</summary>
public class CirculationEventEntity
{
    public long Id { get; set; }

    public Guid ItemId { get; set; }

    public CirculationItemEntity Item { get; set; } = null!;

    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;

    /// <summary><c>Circulation.Event.Wire</c>, e.g. "ready".</summary>
    [MaxLength(32)]
    public string Kind { get; set; } = string.Empty;

    [MaxLength(24)]
    public string? FromState { get; set; }

    [MaxLength(24)]
    public string ToState { get; set; } = string.Empty;

    /// <summary>One line of plain text, from <c>Circulation.describe</c>.</summary>
    [MaxLength(500)]
    public string Detail { get; set; } = string.Empty;
}

public enum NotificationStatus
{
    Pending,
    Sent,
    /// <summary>Gave up after the configured number of attempts.</summary>
    Failed,
    /// <summary>Not sent on purpose: no address, muted, or delivery switched off.</summary>
    Suppressed
}

/// <summary>
/// One message to one person, in the outbox. Written by whoever has something to say and
/// drained by <c>NotificationWorker</c>, so nothing on a request path or inside a sync ever
/// waits on a mail server.
/// </summary>
public class NotificationEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>OIDC subject of the recipient.</summary>
    [MaxLength(200)]
    public string Recipient { get; set; } = string.Empty;

    /// <summary>One of <c>NotificationCategories</c>, e.g. "hold-ready".</summary>
    [MaxLength(32)]
    public string Category { get; set; } = string.Empty;

    /// <summary>What raised it, for the log: "circulation:bibliocommons", "downloads".</summary>
    [MaxLength(100)]
    public string Source { get; set; } = string.Empty;

    [MaxLength(300)]
    public string Subject { get; set; } = string.Empty;

    /// <summary>Plain text. Rendered as text by every channel; never as HTML.</summary>
    public string Body { get; set; } = string.Empty;

    public NotificationStatus Status { get; set; } = NotificationStatus.Pending;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? SentAt { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset? NextAttemptAt { get; set; }

    [MaxLength(500)]
    public string? LastError { get; set; }
}

/// <summary>How one person wants to be told things.</summary>
public class NotificationPreferenceEntity
{
    [MaxLength(200)]
    public string Subject { get; set; } = string.Empty;

    [MaxLength(320)]
    public string? Email { get; set; }

    public bool EmailEnabled { get; set; } = true;

    /// <summary>Comma-separated category keys the person has switched off.</summary>
    [MaxLength(500)]
    public string MutedCategories { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
