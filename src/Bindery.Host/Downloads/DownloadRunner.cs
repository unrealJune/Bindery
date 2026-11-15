using Bindery.Core;
using Bindery.Host.Configuration;
using Bindery.Host.Data;
using Bindery.Host.Library;
using Bindery.Host.Notifications;
using Bindery.Host.Plugins;
using Bindery.Host.Security;
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
    ShelfService shelves,
    BookSources sources,
    NotificationService notifications,
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
        var existing = await FindExistingBookAsync(job, manifest.Name, cancellationToken);
        var known = KnownSource(existing, manifest.Name, job.Url);

        if (existing is not null)
        {
            // Recorded now, so even an "unchanged" answer says which book it was about.
            job.BookId = existing.Id;

            await LogAsync(job, "info", known is null
                ? $"filing into “{existing.Title}” as a new source"
                : $"refreshing “{existing.Title}”");
        }

        // Whose login the plugin uses (§2.3, §4.1): the person who asked, or — for an
        // update nobody asked for — whoever has held the book longest. It decides config
        // only; it never puts the book on anyone's shelf.
        var actingFor = job.RequesterSubject
            ?? (existing is null ? null : (await shelves.OwnersAsync(existing.Id, cancellationToken)).FirstOrDefault());
        var config = await settings.GetForPluginAsync(manifest, actingFor, cancellationToken);

        var request = new Protocol.DownloadRequest(
            job.Id,
            job.Url,
            config.ToFSharpMap(),
            new Protocol.DownloadOptions(
                known is not null,
                (known?.SourceId).ToOption(),
                // "Nothing new since X" is only a fair question when the book's files came
                // from this very source. If they came from another one, the plugin must
                // produce its own copy, or switching sources would never change the book.
                known is not null && FilesCameFrom(existing!, known)
                    ? existing!.UpdatedAt.ToValueOption()
                    : Microsoft.FSharp.Core.FSharpOption<DateTimeOffset>.None,
                manifest.Formats));

        Protocol.DownloadOutcome? outcome = null;
        var lastPersist = DateTimeOffset.MinValue;

        await client.DownloadAsync(
            resolved.Descriptor.Entry,
            request,
            actingFor is null ? null : new Person(actingFor, IsAdmin: false),
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
            // Nothing new to fetch, but someone else's copy is current — and the person who
            // asked for it is owed it on their shelf all the same.
            var shelved = existing is not null
                && job.RequesterSubject is not null
                && await shelves.AddAsync(existing.Id, job.RequesterSubject, cancellationToken);

            job.BookId ??= existing?.Id;
            job.Status = shelved ? JobStatus.Succeeded : JobStatus.Unchanged;
            job.Percent = 100;
            job.Message = shelved ? "Added to your shelf" : "Already up to date";
            job.CompletedAt = DateTimeOffset.UtcNow;
            await LogAsync(job, "info", shelved
                ? "already in the library and current; shelved for the requester"
                : "the source has nothing new");
            await db.SaveChangesAsync(cancellationToken);

            if (shelved)
            {
                await NotifyAsync(
                    job,
                    NotificationCategories.DownloadFiled,
                    $"Filed: {existing!.Title}",
                    $"{existing.Title}\n\nis on the shelf, from {job.Url}.\n\n— Bindery");
            }

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

            IndexedBook indexed;
            Placement placement;

            // Under the shelf gate: the owner list is read from the index and written into a
            // fresh sidecar, and a shelf change in between would be lost with the old one.
            using (await ShelfService.HoldAsync(cancellationToken))
            {
                // Another person's job for the same URL may have filed it while this one ran;
                // both land on the one book, and both people get it.
                existing ??= await FindExistingBookAsync(job, descriptor.Name, cancellationToken);

                // Any book already known by what the plugin says this work is — its
                // canonical URL or its source id — through any plugin.
                existing ??= await sources.FindBookAsync(
                    [job.Url, metadata.SourceUrl.OrNull()], descriptor.Name, metadata.SourceId.OrNull(), cancellationToken);

                // The pasted URL is rarely the plugin's canonical one (a chapter link, a
                // missing www). What the plugin says the work is decides — the same test the
                // library uses to reuse a directory, so a second person's copy can never be
                // filed as a new book over the first person's sidecar.
                existing ??= await FindSameWorkAsync(
                    metadata.SourceId.OrNull(), metadata.SourceUrl.OrNull(), cancellationToken);

                // The requester joins the owners. A job nobody queued keeps whoever has it.
                var owners = await shelves.OwnersForPlacementAsync(existing?.Id, job.RequesterSubject, cancellationToken);
                if (existing is not null)
                {
                    await db.Entry(existing).Collection(book => book.Sources).LoadAsync(cancellationToken);
                }

                var sidecar = BuildSidecar(metadata, descriptor.Name, existing, job) with { Owners = owners };
                placement = await library.PlaceAsync(sidecar, staged, existing?.DirectoryPath, cancellationToken);

                var placed = sidecar with { Files = placement.Files, CoverPath = placement.CoverPath };
                indexed = await indexer.IndexAsync(placed, placement.DirectoryPath, existing, cancellationToken);
            }

            job.BookId = indexed.Book.Id;
            job.Status = JobStatus.Succeeded;
            job.Percent = 100;
            job.Message = "Done";
            job.CompletedAt = DateTimeOffset.UtcNow;
            await LogAsync(job, "info", $"filed as {placement.DirectoryPath}");

            if (indexed.Book.SourcePlugin != descriptor.Name)
            {
                await LogAsync(job, "info",
                    $"updates still follow {indexed.Book.SourcePlugin}; follow this source from the book page to switch");
            }

            // Before the save that marks the job done, so nobody reading a finished job's
            // log can miss the advice.
            if (indexed.WasNew && job.RequesterSubject is not null)
            {
                await NoteDuplicatesAsync(job, indexed.Book, job.RequesterSubject, cancellationToken);
            }

            await db.SaveChangesAsync(cancellationToken);

            if (!job.IsUpdate)
            {
                await NotifyAsync(
                    job,
                    NotificationCategories.DownloadFiled,
                    $"Filed: {indexed.Book.Title}",
                    $"{indexed.Book.Title}\n\nis on the shelf, from {job.Url}.\n\n— Bindery");
            }
        }
        finally
        {
            // The plugin keeps artifacts for a while by contract, but telling it we are
            // finished lets it release the disk now.
            await client.CancelJobAsync(descriptor.Entry, job.Id, CancellationToken.None);
        }
    }

    private static SidecarMetadata BuildSidecar(
        Protocol.BookMetadata metadata,
        string plugin,
        BookEntity? existing,
        DownloadJobEntity job)
    {
        var now = DateTimeOffset.UtcNow;
        var incoming = new SidecarSource(
            plugin, metadata.SourceUrl.OrNull() ?? job.Url, metadata.SourceId.OrNull(), now, now);

        var previous = existing?.Sources.Select(source => new SidecarSource(
            source.Plugin, source.Url, source.SourceId, source.AddedAt, source.LastFetchedAt)) ?? [];

        // The source this download came from becomes the followed one when the book is new,
        // when nothing was followed, when it already was the followed one, or when the
        // person asked. Otherwise it joins the list and following stays where it was.
        var follow = existing is null
                     || job.Follow
                     || existing.SourcePlugin is null
                     || existing.SourceUrl is null
                     || IsSameSource(existing.SourcePlugin, existing.SourceUrl, plugin, job.Url, incoming.Url);

        return new SidecarMetadata
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
            SourceUrl = follow ? incoming.Url : existing!.SourceUrl,
            SourcePlugin = follow ? plugin : existing!.SourcePlugin,
            SourceId = follow ? incoming.SourceId : existing!.SourceId,
            Sources = SidecarSource.Fold(previous.Append(incoming)),
            Published = metadata.Published.OrNullable(),
            AddedAt = existing?.AddedAt ?? now,
            UpdatedAt = metadata.Updated.OrNullable() ?? now
        };
    }

    private static bool IsSameSource(string plugin, string url, string otherPlugin, params string[] otherUrls) =>
        plugin == otherPlugin
        && otherUrls.Any(other => WorkIdentity.normalizeUrl(other) == WorkIdentity.normalizeUrl(url));

    private async Task<BookEntity?> FindExistingBookAsync(
        DownloadJobEntity job,
        string plugin,
        CancellationToken cancellationToken)
    {
        if (job.BookId is { } bookId)
        {
            return await db.Books
                .Include(book => book.Sources)
                .FirstOrDefaultAsync(book => book.Id == bookId, cancellationToken);
        }

        // Before the download only the URL is known; any book that has ever been fetched
        // from it, through any plugin, is this one. Source ids are checked afterwards.
        return await sources.FindBookAsync([job.Url], plugin, sourceId: null, cancellationToken)
               ?? await db.Books
                   .Include(book => book.Sources)
                   .FirstOrDefaultAsync(book => book.SourceUrl == job.Url, cancellationToken);
    }

    /// <summary>The book's existing record of this plugin and URL, if it has one.</summary>
    private static BookSourceEntity? KnownSource(BookEntity? book, string plugin, string url)
    {
        if (book is null)
        {
            return null;
        }

        var normalized = WorkIdentity.normalizeUrl(url);
        var known = book.Sources.FirstOrDefault(source => source.Plugin == plugin && source.NormalizedUrl == normalized);

        if (known is null && book.SourcePlugin == plugin && book.SourceUrl is not null
            && WorkIdentity.normalizeUrl(book.SourceUrl) == normalized)
        {
            // Indexed before sources existed: the followed source is still known.
            known = new BookSourceEntity
            {
                Plugin = plugin,
                Url = book.SourceUrl,
                NormalizedUrl = normalized,
                SourceId = book.SourceId,
                LastFetchedAt = book.UpdatedAt
            };
        }

        return known;
    }

    /// <summary>Were the book's current files produced by this source?</summary>
    private static bool FilesCameFrom(BookEntity book, BookSourceEntity source)
    {
        var latest = book.Sources.Max(candidate => candidate.LastFetchedAt);
        return latest is null || source.LastFetchedAt is null || source.LastFetchedAt >= latest;
    }

    /// <summary>
    /// A new book that looks like one already on the requester's shelf is filed anyway (a
    /// title match is not proof) and the job says so, pointing at the merge on the book
    /// page. Only the requester's own books are compared: what is on anyone else's shelf
    /// is theirs to know.
    /// </summary>
    private async Task NoteDuplicatesAsync(
        DownloadJobEntity job,
        BookEntity book,
        string owner,
        CancellationToken cancellationToken)
    {
        try
        {
            var found = await sources.DuplicatesAsync(owner, book.Id, cancellationToken);

            foreach (var duplicate in found.Take(3))
            {
                await LogAsync(job, "warn",
                    $"looks like “{duplicate.Title}” by {duplicate.AuthorLine}, already on your shelf "
                    + $"({duplicate.Reason}); merge them from the book page");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Advice, not part of filing. A failure here must not fail the download.
            logger.LogWarning(ex, "duplicate check failed for {Book}", book.Id);
        }
    }

    private async Task<BookEntity?> FindSameWorkAsync(string? sourceId, string? sourceUrl, CancellationToken cancellationToken)
    {
        if (sourceId is null && sourceUrl is null)
        {
            return null;
        }

        return await db.Books.FirstOrDefaultAsync(
            book => (sourceId != null && book.SourceId == sourceId) || (sourceUrl != null && book.SourceUrl == sourceUrl),
            cancellationToken);
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

        if (job.Status == JobStatus.Failed)
        {
            await NotifyAsync(
                job,
                NotificationCategories.DownloadFailed,
                $"Download failed: {job.Url}",
                $"{job.Url}\n\ncould not be downloaded and will not be retried.\n\n{code}: {message}\n\n— Bindery");
        }
    }

    /// <summary>
    /// Tells whoever queued the job how it ended. Scheduled updates and plugin hints have no
    /// person behind them and are never announced; a notification must never be able to
    /// fail the download it is about.
    /// </summary>
    private async Task NotifyAsync(DownloadJobEntity job, string category, string subject, string body)
    {
        if (string.IsNullOrEmpty(job.RequesterSubject))
        {
            return;
        }

        try
        {
            await notifications.EnqueueAsync(
                job.RequesterSubject, category, $"downloads:{job.PluginName}", subject, body, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "could not queue a notification for job {Job}", job.Id);
        }
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
