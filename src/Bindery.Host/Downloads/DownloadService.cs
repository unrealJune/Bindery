using Bindery.Host.Data;
using Bindery.Host.Plugins;
using Microsoft.EntityFrameworkCore;

namespace Bindery.Host.Downloads;

public sealed record EnqueueResult(DownloadJobEntity? Job, string? Problem)
{
    public bool Accepted => Job is not null;
}

/// <summary>
/// Queueing, cancelling, and retrying downloads. The request-thread half of the pipeline.
/// </summary>
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
        CancellationToken cancellationToken)
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

        var duplicate = await db.Jobs
            .Where(job => job.Url == url && (job.Status == JobStatus.Queued || job.Status == JobStatus.Running))
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
            RequestedBy = requestedBy
        };

        db.Jobs.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        await queue.EnqueueAsync(entity.Id, cancellationToken);

        logger.LogInformation("queued {Url} for plugin {Plugin} as job {Job}", url, resolved.Descriptor.Name, entity.Id);
        return new EnqueueResult(entity, null);
    }

    /// <summary>Queues a refresh of a book Bindery already holds.</summary>
    public async Task<EnqueueResult> EnqueueUpdateAsync(
        Guid bookId,
        string? requestedBy,
        CancellationToken cancellationToken)
    {
        var book = await db.Books.FirstOrDefaultAsync(candidate => candidate.Id == bookId, cancellationToken);

        if (book is null)
        {
            return new EnqueueResult(null, "That book is gone.");
        }

        if (string.IsNullOrWhiteSpace(book.SourceUrl))
        {
            return new EnqueueResult(null, "This book has no source URL, so there is nothing to refresh from.");
        }

        var result = await EnqueueAsync(book.SourceUrl, requestedBy, book.SourcePlugin, cancellationToken);

        if (result.Job is not null)
        {
            result.Job.IsUpdate = true;
            result.Job.BookId = book.Id;
            await db.SaveChangesAsync(cancellationToken);
        }

        return result;
    }

    public async Task<bool> CancelAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.FirstOrDefaultAsync(candidate => candidate.Id == jobId, cancellationToken);

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

    public async Task<bool> RetryAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.FirstOrDefaultAsync(candidate => candidate.Id == jobId, cancellationToken);

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

    public Task<List<DownloadJobEntity>> RecentAsync(int limit, CancellationToken cancellationToken) =>
        db.Jobs
            .AsNoTracking()
            .OrderByDescending(job => job.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public Task<List<DownloadJobEntity>> ActiveAsync(CancellationToken cancellationToken) =>
        db.Jobs
            .AsNoTracking()
            .Where(job => job.Status == JobStatus.Queued || job.Status == JobStatus.Running)
            .OrderBy(job => job.CreatedAt)
            .ToListAsync(cancellationToken);

    public Task<DownloadJobEntity?> FindAsync(Guid jobId, CancellationToken cancellationToken) =>
        db.Jobs
            .AsNoTracking()
            .Include(job => job.Log.OrderBy(entry => entry.At))
            .FirstOrDefaultAsync(job => job.Id == jobId, cancellationToken);
}
