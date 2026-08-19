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

    public IReadOnlyList<PluginDescriptor> All =>
        [.. _descriptors.Values.OrderByDescending(d => d.Manifest?.Priority ?? 0).ThenBy(d => d.Name, StringComparer.Ordinal)];

    public IReadOnlyList<PluginDescriptor> Usable => [.. All.Where(descriptor => descriptor.IsUsable)];

    public PluginDescriptor? Find(string name) =>
        _descriptors.TryGetValue(name, out var descriptor) ? descriptor : null;

    public PluginEntry? FindEntry(string name) => Find(name)?.Entry;

    /// <summary>Refreshes every enabled plugin's manifest, in parallel.</summary>
    public async Task RefreshAllAsync(CancellationToken cancellationToken)
    {
        var configured = options.Value.Plugins.Registry.Where(entry => entry.Enabled).ToList();

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
        catch (Exception ex) when (ex is PluginTransportException or HttpRequestException or OperationCanceledException)
        {
            var previous = Find(entry.Name);

            // Keep the last good manifest: a plugin restarting should not make its books
            // unroutable for the duration.
            descriptor = new PluginDescriptor(
                entry, previous?.Manifest, previous?.Compiled, previous?.RefreshedAt, ex.Message);

            logger.LogWarning(ex, "plugin {Plugin} is unreachable or unusable", entry.Name);
        }

        _descriptors[entry.Name] = descriptor;
        return descriptor;
    }

    public TimeSpan RefreshInterval => _options.ManifestRefresh;
}

/// <summary>
/// Keeps manifests fresh so that bumping a plugin image is visible without restarting
/// Bindery.
/// </summary>
public sealed class PluginRefreshService(PluginRegistry registry, ILogger<PluginRefreshService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // First pass immediately: the UI should know what is installed before anyone looks.
        await SafeRefreshAsync(stoppingToken);

        using var timer = new PeriodicTimer(registry.RefreshInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await SafeRefreshAsync(stoppingToken);
        }
    }

    private async Task SafeRefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            await registry.RefreshAllAsync(cancellationToken);
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
