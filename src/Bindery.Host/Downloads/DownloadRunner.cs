using Bindery.Core;
using Bindery.Host.Configuration;
using Bindery.Host.Data;
using Bindery.Host.Library;
using Bindery.Host.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bindery.Host.Downloads;

/// <summary>
/// Runs one download from end to end: resolve, stream, fetch artifacts, file them, index.
/// </summary>
/// <remarks>
/// Order matters here. Files are moved into the library *before* the database is touched,
/// because the files are the source of truth: a crash between the two leaves a book on
/// disk that a rescan finds, whereas the reverse leaves a row pointing at nothing.
/// </remarks>
public sealed class DownloadRunner(
    BinderyDbContext db,
    PluginResolver resolver,
    PluginClient client,
    PluginSettingsStore settings,
    LibraryStore library,
    BookIndexer indexer,
    IOptions<BinderyOptions> options,
    ILogger<DownloadRunner> logger)
{
    private readonly DownloadOptions _downloads = options.Value.Downloads;

    public async Task RunAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.FirstOrDefaultAsync(candidate => candidate.Id == jobId, cancellationToken);

        if (job is null)
        {
            return;
        }

        if (job.Status is JobStatus.Cancelled or JobStatus.Succeeded or JobStatus.Unchanged)
        {
            return;
        }

        job.Status = JobStatus.Running;
        job.StartedAt = DateTimeOffset.UtcNow;
        job.Attempts += 1;
        job.Percent = 0;
        job.Message = "Starting";
        await db.SaveChangesAsync(cancellationToken);

        var staging = Path.Combine(options.Value.DataPath, "staging", jobId.ToString("N"));

        try
        {
            await ExecuteAsync(job, staging, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await MarkCancelledAsync(job);
            throw;
        }
        catch (PluginTransportException ex)
        {
            logger.LogWarning(ex, "job {Job} failed at the transport level", jobId);
            await FailAsync(job, "internal", ex.Message, retryable: true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "job {Job} failed unexpectedly", jobId);
            await FailAsync(job, "internal", $"{ex.GetType().Name}: {ex.Message}", retryable: false);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    private async Task ExecuteAsync(DownloadJobEntity job, string staging, CancellationToken cancellationToken)
    {
        var resolved = await resolver.ResolveAsync(job.Url, job.PluginName, cancellationToken);

        if (resolved is null)
        {
            await FailAsync(job, "unsupported_url", "No installed plugin claims that URL.", retryable: false);
            return;
        }

        job.PluginName = resolved.Descriptor.Name;
        await LogAsync(job, "info", $"routed to {resolved.Descriptor.DisplayName}: {resolved.Reason}");

        var manifest = resolved.Descriptor.Manifest!;
        var config = await settings.GetForPluginAsync(manifest.Name, cancellationToken);
        var existing = await FindExistingBookAsync(job, cancellationToken);

        var request = new Protocol.DownloadRequest(
            job.Id,
            job.Url,
            config.ToFSharpMap(),
            new Protocol.DownloadOptions(
                job.IsUpdate || existing is not null,
                (existing?.SourceId).ToOption(),
                existing?.UpdatedAt.ToValueOption() ?? Microsoft.FSharp.Core.FSharpOption<DateTimeOffset>.None,
                manifest.Formats));

        Protocol.DownloadOutcome? outcome = null;
        var lastPersist = DateTimeOffset.MinValue;

        await client.DownloadAsync(
            resolved.Descriptor.Entry,
            request,
            async (pluginEvent, token) =>
            {
                switch (pluginEvent)
                {
                    case Protocol.PluginEvent.Progress progress:
                        job.Percent = progress.percent.OrNullable() ?? job.Percent;
                        job.Message = progress.message.OrNull() ?? job.Message;

                        // Persisting every event would turn a chatty plugin into a write
                        // storm on a SQLite file; once a second is plenty for a progress bar.
                        if (DateTimeOffset.UtcNow - lastPersist > TimeSpan.FromSeconds(1))
                        {
                            lastPersist = DateTimeOffset.UtcNow;
                            await db.SaveChangesAsync(token);
                        }

                        break;

                    case Protocol.PluginEvent.LogLine line:
                        await LogAsync(job, line.level.Wire, line.message);
                        break;

                    case Protocol.PluginEvent.Result result:
                        outcome = result.outcome;
                        break;

                    case Protocol.PluginEvent.Unrecognized unknown:
                        // Required by the protocol: unknown event kinds are ignored, not
                        // fatal, so a newer plugin stays usable on an older host.
                        logger.LogDebug("ignoring unknown event '{Kind}' from {Plugin}", unknown.kind, manifest.Name);
                        break;
                }
            },
            cancellationToken);

        if (outcome is null)
        {
            await FailAsync(job, "internal", "The plugin finished without a result.", retryable: true);
            return;
        }

        if (outcome.IsUnchanged)
        {
            job.Status = JobStatus.Unchanged;
            job.Percent = 100;
            job.Message = "Already up to date";
            job.CompletedAt = DateTimeOffset.UtcNow;
            await LogAsync(job, "info", "the source has nothing new");
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        switch (outcome)
        {
            case Protocol.DownloadOutcome.Failed failure:
                await FailAsync(job, failure.error.Code.Wire, failure.error.Message, failure.error.Retryable);
                break;

            case Protocol.DownloadOutcome.Succeeded success:
                await StoreAsync(job, resolved.Descriptor, success, existing, staging, cancellationToken);
                break;
        }
    }

    private async Task StoreAsync(
        DownloadJobEntity job,
        PluginDescriptor descriptor,
        Protocol.DownloadOutcome.Succeeded success,
        BookEntity? existing,
        string staging,
        CancellationToken cancellationToken)
    {
        job.Message = "Fetching files";
        job.Percent = Math.Max(job.Percent, 96);
        await db.SaveChangesAsync(cancellationToken);

        var artifacts = success.artifacts.AsList();
        var metadata = success.metadata;
        var staged = new List<StagedFile>();

        try
        {
            foreach (var artifact in artifacts)
            {
                var fetched = await client.FetchArtifactAsync(descriptor.Entry, job.Id, artifact, staging, cancellationToken);

                staged.Add(new StagedFile(
                    fetched.TempPath,
                    artifact.Format,
                    fetched.ContentType,
                    artifact.Filename,
                    fetched.SizeBytes,
                    fetched.Sha256,
                    artifact.Kind.IsCoverArtifact));
            }

            var sidecar = BuildSidecar(metadata, descriptor.Name, existing);
            var placement = await library.PlaceAsync(sidecar, staged, existing?.DirectoryPath, cancellationToken);

            var placed = sidecar with { Files = placement.Files, CoverPath = placement.CoverPath };
            var indexed = await indexer.IndexAsync(placed, placement.DirectoryPath, existing, cancellationToken);

            job.BookId = indexed.Book.Id;
            job.Status = JobStatus.Succeeded;
            job.Percent = 100;
            job.Message = "Done";
            job.CompletedAt = DateTimeOffset.UtcNow;
            await LogAsync(job, "info", $"filed as {placement.DirectoryPath}");
            await db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            // The plugin keeps artifacts for a while by contract, but telling it we are
            // finished lets it release the disk now.
            await client.CancelJobAsync(descriptor.Entry, job.Id, CancellationToken.None);
        }
    }

    private SidecarMetadata BuildSidecar(Protocol.BookMetadata metadata, string plugin, BookEntity? existing) =>
        new()
        {
            Id = existing?.Id ?? Guid.NewGuid(),
            Title = metadata.Title,
            Authors = [.. metadata.Authors.AsList()],
            Series = metadata.Series.OrNull(),
            SeriesIndex = metadata.SeriesIndex.OrNullable(),
            Summary = metadata.Summary.OrNull(),
            Language = metadata.Language.OrNull(),
            Tags = [.. metadata.Tags.AsList()],
            Chapters = metadata.Chapters.OrNullable(),
            SourceUrl = metadata.SourceUrl.OrNull(),
            SourcePlugin = plugin,
            SourceId = metadata.SourceId.OrNull(),
            Published = metadata.Published.OrNullable(),
            AddedAt = existing?.AddedAt ?? DateTimeOffset.UtcNow,
            UpdatedAt = metadata.Updated.OrNullable() ?? DateTimeOffset.UtcNow
        };

    private async Task<BookEntity?> FindExistingBookAsync(DownloadJobEntity job, CancellationToken cancellationToken)
    {
        if (job.BookId is { } bookId)
        {
            return await db.Books.FirstOrDefaultAsync(book => book.Id == bookId, cancellationToken);
        }

        // Same URL is the reliable signal before a download; source id only exists after
        // the plugin has told us one.
        return await db.Books.FirstOrDefaultAsync(book => book.SourceUrl == job.Url, cancellationToken);
    }

    private async Task FailAsync(DownloadJobEntity job, string code, string message, bool retryable)
    {
        job.ErrorCode = code;
        job.ErrorMessage = Truncate(message, 1900);
        job.Retryable = retryable;
        job.Message = Truncate(message, 400);
        job.CompletedAt = DateTimeOffset.UtcNow;

        if (retryable && job.Attempts < _downloads.MaxAttempts)
        {
            // Exponential backoff, scheduled in the database so a restart does not lose it.
            var delay = TimeSpan.FromTicks(_downloads.RetryDelay.Ticks * (long)Math.Pow(2, job.Attempts - 1));
            job.Status = JobStatus.Queued;
            job.NextAttemptAt = DateTimeOffset.UtcNow + delay;
            job.CompletedAt = null;
            await LogAsync(job, "warn", $"{code}: {message} — retrying in {delay.TotalSeconds:0}s");
        }
        else
        {
            job.Status = JobStatus.Failed;
            job.NextAttemptAt = null;
            await LogAsync(job, "error", $"{code}: {message}");
        }

        await db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task MarkCancelledAsync(DownloadJobEntity job)
    {
        job.Status = JobStatus.Cancelled;
        job.CompletedAt = DateTimeOffset.UtcNow;
        job.Message = "Cancelled";
        job.NextAttemptAt = null;
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private Task LogAsync(DownloadJobEntity job, string level, string message)
    {
        db.JobLog.Add(new JobLogEntity
        {
            JobId = job.Id,
            Level = level,
            Message = Truncate(message, 1900)
        });

        return Task.CompletedTask;
    }

    private static string Truncate(string value, int limit) =>
        value.Length <= limit ? value : value[..limit];

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Swept on the next boot.
        }
    }
}
