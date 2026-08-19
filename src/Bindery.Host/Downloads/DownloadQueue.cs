using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Bindery.Host.Downloads;

/// <summary>
/// The in-process work queue and the handles that let a running job be cancelled.
/// </summary>
/// <remarks>
/// The queue is a hint, not the record: the database is authoritative about what is
/// pending, so a restart loses nothing. That is why <see cref="DownloadWorker"/> requeues
/// from the database at boot rather than trusting anything held here.
/// </remarks>
public sealed class DownloadQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false
    });

    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();

    public ValueTask EnqueueAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(jobId, cancellationToken);

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);

    /// <summary>Registers a running job so it can be cancelled from a request thread.</summary>
    public CancellationTokenSource Track(Guid jobId, CancellationToken linkedTo)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(linkedTo);
        _running[jobId] = source;
        return source;
    }

    public void Release(Guid jobId)
    {
        if (_running.TryRemove(jobId, out var source))
        {
            source.Dispose();
        }
    }

    /// <summary>
    /// Cancels a running job. Returns false when the job is not currently running, which
    /// the caller treats as "cancel the queued row instead".
    /// </summary>
    public bool Cancel(Guid jobId)
    {
        if (!_running.TryGetValue(jobId, out var source))
        {
            return false;
        }

        try
        {
            source.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public bool IsRunning(Guid jobId) => _running.ContainsKey(jobId);

    public int RunningCount => _running.Count;
}
