using System.Collections.Concurrent;
using Bindery.Core;
using Bindery.Host.Configuration;
using Microsoft.Extensions.Options;

namespace Bindery.Host.Plugins;

/// <summary>
/// A configured plugin and what it says about itself.
/// </summary>
public sealed record PluginDescriptor(
    PluginEntry Entry,
    Protocol.Manifest? Manifest,
    Routing.CompiledPlugin? Compiled,
    DateTimeOffset? RefreshedAt,
    string? Error)
{
    public string Name => Entry.Name;

    public bool IsUsable => Manifest is not null && Error is null;

    public string DisplayName => Manifest?.DisplayName ?? Entry.Name;
}

/// <summary>
/// The set of plugins Bindery knows about.
/// </summary>
/// <remarks>
/// Plugins come from configuration and nowhere else. There is no directory scan, no
/// manifest file to mount, and no discovery protocol: the Helm chart renders a ConfigMap
/// and this reads it at boot. Each plugin is then asked what it is, over HTTP.
/// </remarks>
public sealed class PluginRegistry(
    PluginClient client,
    IOptions<BinderyOptions> options,
    ILogger<PluginRegistry> logger)
{
    private readonly ConcurrentDictionary<string, PluginDescriptor> _descriptors = new(StringComparer.Ordinal);
    private readonly PluginHostOptions _options = options.Value.Plugins;
    private TaskCompletionSource _fault = NewSignal();

    public IReadOnlyList<PluginDescriptor> All =>
        [.. _descriptors.Values.OrderByDescending(d => d.Manifest?.Priority ?? 0).ThenBy(d => d.Name, StringComparer.Ordinal)];

    public IReadOnlyList<PluginDescriptor> Usable => [.. All.Where(descriptor => descriptor.IsUsable)];

    public PluginDescriptor? Find(string name) =>
        _descriptors.TryGetValue(name, out var descriptor) ? descriptor : null;

    public PluginEntry? FindEntry(string name) => Find(name)?.Entry;

    /// <summary>Configured plugins that are not usable right now, including ones never reached.</summary>
    public IReadOnlyList<PluginEntry> Unusable =>
        [.. Configured().Where(entry => Find(entry.Name) is not { IsUsable: true })];

    /// <summary>
    /// A task that completes the next time any refresh — background or someone pressing
    /// the button — finds a plugin unusable.
    /// </summary>
    public Task ArmFaultSignal()
    {
        var fresh = NewSignal();
        Interlocked.Exchange(ref _fault, fresh);
        return fresh.Task;
    }

    /// <summary>Retries only the plugins that are down, leaving healthy ones alone.</summary>
    public Task RefreshUnusableAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(Unusable.Select(entry => RefreshAsync(entry, cancellationToken)));

    private List<PluginEntry> Configured() => [.. options.Value.Plugins.Registry.Where(entry => entry.Enabled)];

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Refreshes every enabled plugin's manifest, in parallel.</summary>
    public async Task RefreshAllAsync(CancellationToken cancellationToken)
    {
        var configured = Configured();

        foreach (var stale in _descriptors.Keys.Except(configured.Select(entry => entry.Name), StringComparer.Ordinal))
        {
            _descriptors.TryRemove(stale, out _);
            logger.LogInformation("plugin {Plugin} is no longer configured", stale);
        }

        await Task.WhenAll(configured.Select(entry => RefreshAsync(entry, cancellationToken)));
    }

    public async Task<PluginDescriptor> RefreshAsync(PluginEntry entry, CancellationToken cancellationToken)
    {
        PluginDescriptor descriptor;

        try
        {
            var manifest = await client.GetManifestAsync(entry, cancellationToken);
            var compiled = Routing.compile(manifest);

            foreach (var (pattern, reason) in compiled.RejectedPatterns.AsList())
            {
                // A rejected pattern disables one rule, never the plugin. Say so loudly
                // anyway: from the outside it looks like the plugin simply stopped
                // claiming a site.
                logger.LogWarning(
                    "plugin {Plugin} declares an unusable URL pattern {Pattern}: {Reason}", entry.Name, pattern, reason);
            }

            descriptor = new PluginDescriptor(entry, manifest, compiled, DateTimeOffset.UtcNow, null);
            logger.LogInformation(
                "plugin {Plugin} v{Version} ready: {Patterns} pattern(s), ui {Ui}",
                manifest.Name, manifest.Version, compiled.Patterns.Length, manifest.Ui.Mode.Wire);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Anything but shutdown is the plugin being unusable. A narrower filter let an
            // unexpected exception escape, which left the plugin with no descriptor at all.
            var previous = Find(entry.Name);

            // Keep the last good manifest: a plugin restarting should not make its books
            // unroutable for the duration.
            descriptor = new PluginDescriptor(
                entry, previous?.Manifest, previous?.Compiled, previous?.RefreshedAt, ex.Message);

            logger.LogWarning(ex, "plugin {Plugin} is unreachable or unusable", entry.Name);
        }

        _descriptors[entry.Name] = descriptor;

        if (!descriptor.IsUsable)
        {
            Volatile.Read(ref _fault).TrySetResult();
        }

        return descriptor;
    }

    public TimeSpan RefreshInterval => _options.ManifestRefresh;

    /// <summary>How long to wait before retrying after <paramref name="failures"/> failed retries.</summary>
    public TimeSpan RetryDelay(int failures)
    {
        var delay = _options.ManifestRetry.Ticks * Math.Pow(2, Math.Min(failures, 30));
        return TimeSpan.FromTicks((long)Math.Min(delay, _options.ManifestRetryMax.Ticks));
    }
}

/// <summary>
/// Keeps manifests fresh so that bumping a plugin image is visible without restarting
/// Bindery, and reconnects plugins that are down.
/// </summary>
/// <remarks>
/// Sidecars start alongside Bindery, so the first pass often finds a plugin not listening
/// yet; a plugin can also restart under a running host. Waiting the full refresh interval
/// to look again left it unusable until someone pressed the button. Instead, whenever a
/// plugin is unusable, only the unusable ones are retried with exponential backoff, and a
/// refresh that newly finds one down restarts the backoff from the shortest delay.
/// </remarks>
public sealed class PluginRefreshService(PluginRegistry registry, ILogger<PluginRefreshService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // First pass immediately: the UI should know what is installed before anyone looks.
        await SafeAsync(registry.RefreshAllAsync, stoppingToken);

        var nextFull = DateTimeOffset.UtcNow + registry.RefreshInterval;
        var failures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            var fault = registry.ArmFaultSignal();
            var wait = nextFull - DateTimeOffset.UtcNow;

            if (registry.Unusable.Count > 0)
            {
                var retry = registry.RetryDelay(failures);
                wait = retry < wait ? retry : wait;
            }

            var delay = Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, stoppingToken);

            if (await Task.WhenAny(delay, fault) == fault)
            {
                // Something fell over just now: back off from the start, not the top.
                failures = 0;
                continue;
            }

            try
            {
                await delay;
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (DateTimeOffset.UtcNow >= nextFull)
            {
                await SafeAsync(registry.RefreshAllAsync, stoppingToken);
                nextFull = DateTimeOffset.UtcNow + registry.RefreshInterval;
            }
            else
            {
                await SafeAsync(registry.RefreshUnusableAsync, stoppingToken);
            }

            failures = registry.Unusable.Count > 0 ? failures + 1 : 0;
        }
    }

    private async Task SafeAsync(Func<CancellationToken, Task> refresh, CancellationToken cancellationToken)
    {
        try
        {
            await refresh(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "plugin refresh failed");
        }
    }
}
