using Bindery.Host.Data;
using Bindery.Host.Plugins;
using Bindery.Host.Security;
using Microsoft.EntityFrameworkCore;

namespace Bindery.Host.Downloads;

public sealed record EnqueueResult(DownloadJobEntity? Job, string? Problem)
{
    public bool Accepted => Job is not null;
}

/// <summary>
/// A job aimed at a book Bindery already holds. Set on the row before it is queued, so the
/// worker can never pick the job up without it.
/// </summary>
public sealed record JobTarget(Guid BookId, bool IsUpdate, bool Follow);

/// <summary>
/// Queueing, cancelling, and retrying downloads. The request-thread half of the pipeline.
/// </summary>
/// <remarks>
/// A job belongs to whoever queued it. A member sees their own, plus the jobs nobody queued
/// — scheduled updates, plugin hints — for books on their shelf; an admin sees them all.
/// Only an admin acts on a job someone else queued.
/// </remarks>
public sealed class DownloadService(
    BinderyDbContext db,
    DownloadQueue queue,
    PluginResolver resolver,
    ILogger<DownloadService> logger)
{
    public async Task<EnqueueResult> EnqueueAsync(
        string url,
        string? requestedBy,
        string? preferredPlugin,
        CancellationToken cancellationToken,
        string? requesterSubject = null,
        JobTarget? target = null)
    {
        url = (url ?? string.Empty).Trim();

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            return new EnqueueResult(null, "That does not look like an http or https URL.");
        }

        // Resolve up front so the user is told immediately that nothing handles the URL,
        // rather than discovering it in a failed job thirty seconds later.
        var resolved = await resolver.ResolveAsync(url, preferredPlugin, cancellationToken);

        if (resolved is null)
        {
            return new EnqueueResult(null, "No installed plugin claims that URL.");
        }

        // Per requester: someone else's job for the same URL is not this person's, and would
        // not put the book on their shelf. The runner reconciles the two onto one book.
        var duplicate = await db.Jobs
            .Where(job => job.Url == url
                && job.RequesterSubject == requesterSubject
                && (job.Status == JobStatus.Queued || job.Status == JobStatus.Running))
            .FirstOrDefaultAsync(cancellationToken);

        if (duplicate is not null)
        {
            return new EnqueueResult(duplicate, "That URL is already queued.");
        }

        var entity = new DownloadJobEntity
        {
            Url = url,
            PluginName = resolved.Descriptor.Name,
            Status = JobStatus.Queued,
            RequestedBy = requestedBy,
            RequesterSubject = requesterSubject,
            BookId = target?.BookId,
            IsUpdate = target?.IsUpdate ?? false,
            Follow = target?.Follow ?? false
        };

        db.Jobs.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        await queue.EnqueueAsync(entity.Id, cancellationToken);

        logger.LogInformation("queued {Url} for plugin {Plugin} as job {Job}", url, resolved.Descriptor.Name, entity.Id);
        return new EnqueueResult(entity, null);
    }

    /// <summary>
    /// Queues a refresh of a book Bindery already holds. A person may only refresh a book on
    /// their own shelf; with no requester — the scheduler, a plugin hint — any book.
    /// </summary>
    public async Task<EnqueueResult> EnqueueUpdateAsync(
        Guid bookId,
        string? requestedBy,
        CancellationToken cancellationToken,
        string? requesterSubject = null)
    {
        var book = await db.Books
            .Where(candidate => requesterSubject == null || candidate.Shelf.Any(entry => entry.Owner == requesterSubject))
            .FirstOrDefaultAsync(candidate => candidate.Id == bookId, cancellationToken);

        if (book is null)
        {
            return new EnqueueResult(null, "That book is gone.");
        }

        if (string.IsNullOrWhiteSpace(book.SourceUrl))
        {
            return new EnqueueResult(null, "This book has no source URL, so there is nothing to refresh from.");
        }

        return await EnqueueAsync(
            book.SourceUrl, requestedBy, book.SourcePlugin, cancellationToken, requesterSubject,
            new JobTarget(book.Id, IsUpdate: true, Follow: false));
    }

    /// <summary>
    /// Downloads a URL — through any plugin, or the one named — into a book Bindery already
    /// holds, replacing its files. The URL becomes one of the book's sources; with
    /// <paramref name="follow"/> it also becomes the one future updates come from.
    /// </summary>
    public async Task<EnqueueResult> EnqueueIntoBookAsync(
        Guid bookId,
        string url,
        string? preferredPlugin,
        bool follow,
        string? requestedBy,
        CancellationToken cancellationToken,
        string? requesterSubject = null)
    {
        // Only a book on the asker's shelf; anyone else's is "gone", never "forbidden".
        if (!await db.Books.AnyAsync(
                candidate => candidate.Id == bookId
                             && (requesterSubject == null || candidate.Shelf.Any(entry => entry.Owner == requesterSubject)),
                cancellationToken))
        {
            return new EnqueueResult(null, "That book is gone.");
        }

        return await EnqueueAsync(
            url, requestedBy, preferredPlugin, cancellationToken, requesterSubject,
            new JobTarget(bookId, IsUpdate: true, Follow: follow));
    }

    /// <summary>Re-fetches one of a book's sources into it, whether or not it is followed.</summary>
    public async Task<EnqueueResult> EnqueueSourceAsync(
        Guid bookId,
        int sourceId,
        bool follow,
        string? requestedBy,
        CancellationToken cancellationToken,
        string? requesterSubject = null)
    {
        var source = await db.BookSources
            .AsNoTracking()
            .Where(candidate => requesterSubject == null
                                || candidate.Book.Shelf.Any(entry => entry.Owner == requesterSubject))
            .FirstOrDefaultAsync(candidate => candidate.Id == sourceId && candidate.BookId == bookId, cancellationToken);

        return source is null
            ? new EnqueueResult(null, "That source is not one of this book's.")
            : await EnqueueAsync(
                source.Url, requestedBy, source.Plugin, cancellationToken, requesterSubject,
                new JobTarget(bookId, IsUpdate: true, Follow: follow));
    }

    public async Task<bool> CancelAsync(Person person, Guid jobId, CancellationToken cancellationToken)
    {
        var job = await ActionableBy(person).FirstOrDefaultAsync(candidate => candidate.Id == jobId, cancellationToken);

        if (job is null || job.Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled or JobStatus.Unchanged)
        {
            return false;
        }

        // Cancelling a running job drops the plugin connection, which is how the protocol
        // says "stop". Cancelling a queued one just marks the row; the worker skips it.
        queue.Cancel(jobId);

        job.Status = JobStatus.Cancelled;
        job.CompletedAt = DateTimeOffset.UtcNow;
        job.Message = "Cancelled";
        job.NextAttemptAt = null;
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("cancelled job {Job}", jobId);
        return true;
    }

    public async Task<bool> RetryAsync(Person person, Guid jobId, CancellationToken cancellationToken)
    {
        var job = await ActionableBy(person).FirstOrDefaultAsync(candidate => candidate.Id == jobId, cancellationToken);

        if (job is null || job.Status is JobStatus.Queued or JobStatus.Running)
        {
            return false;
        }

        job.Status = JobStatus.Queued;
        job.Percent = 0;
        job.Message = null;
        job.ErrorCode = null;
        job.ErrorMessage = null;
        job.CompletedAt = null;
        job.NextAttemptAt = null;
        job.Attempts = 0;
        await db.SaveChangesAsync(cancellationToken);

        await queue.EnqueueAsync(job.Id, cancellationToken);
        return true;
    }

    public Task<List<DownloadJobEntity>> RecentAsync(Person person, int limit, CancellationToken cancellationToken) =>
        VisibleTo(person)
            .AsNoTracking()
            .OrderByDescending(job => job.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

    /// <summary>The jobs about one book, as far as <paramref name="person"/> may see them.</summary>
    public Task<List<DownloadJobEntity>> ForBookAsync(Person person, Guid bookId, int limit, CancellationToken cancellationToken) =>
        VisibleTo(person)
            .AsNoTracking()
            .Where(job => job.BookId == bookId)
            .OrderByDescending(job => job.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public Task<List<DownloadJobEntity>> ActiveAsync(Person person, CancellationToken cancellationToken) =>
        VisibleTo(person)
            .AsNoTracking()
            .Where(job => job.Status == JobStatus.Queued || job.Status == JobStatus.Running)
            .OrderBy(job => job.CreatedAt)
            .ToListAsync(cancellationToken);

    public Task<DownloadJobEntity?> FindAsync(Person person, Guid jobId, CancellationToken cancellationToken) =>
        VisibleTo(person)
            .AsNoTracking()
            .Include(job => job.Log.OrderBy(entry => entry.At))
            .FirstOrDefaultAsync(job => job.Id == jobId, cancellationToken);

    private IQueryable<DownloadJobEntity> VisibleTo(Person person)
    {
        if (person.IsAdmin)
        {
            return db.Jobs;
        }

        var subject = person.Subject;

        return db.Jobs.Where(job =>
            job.RequesterSubject == subject
            || (job.RequesterSubject == null
                && job.BookId != null
                && db.Shelves.Any(entry => entry.BookId == job.BookId && entry.Owner == subject)));
    }

    private IQueryable<DownloadJobEntity> ActionableBy(Person person) =>
        person.IsAdmin ? db.Jobs : db.Jobs.Where(job => job.RequesterSubject == person.Subject);
}
