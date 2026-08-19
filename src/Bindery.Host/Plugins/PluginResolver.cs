using Bindery.Core;

namespace Bindery.Host.Plugins;

public sealed record ResolvedPlugin(PluginDescriptor Descriptor, double Confidence, string Reason);

/// <summary>
/// Decides which plugin gets a URL.
/// </summary>
/// <remarks>
/// The ordering rules live in <c>Bindery.Core.Routing</c>, which is a pure function of
/// manifests and a string. What is here is the part that needs the network: asking a
/// plugin to confirm, and coping when it does not answer.
/// </remarks>
public sealed class PluginResolver(PluginRegistry registry, PluginClient client, ILogger<PluginResolver> logger)
{
    public async Task<ResolvedPlugin?> ResolveAsync(
        string url,
        string? preferredPlugin,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(preferredPlugin))
        {
            var chosen = registry.Find(preferredPlugin);

            // An explicit choice is honoured without probing. The user said so.
            return chosen is { IsUsable: true } ? new ResolvedPlugin(chosen, 1.0, "chosen explicitly") : null;
        }

        var usable = registry.Usable;
        var compiled = usable.Select(descriptor => descriptor.Compiled!).ToFSharpList();
        var candidates = Routing.candidates(compiled, url).AsList();

        foreach (var candidate in candidates)
        {
            var descriptor = usable.FirstOrDefault(d => d.Name == candidate.Plugin);

            if (descriptor is null)
            {
                continue;
            }

            if (!candidate.NeedsProbe)
            {
                return new ResolvedPlugin(descriptor, candidate.Confidence, candidate.Reason);
            }

            var probe = await client.ProbeAsync(descriptor.Entry, url, cancellationToken);

            if (probe is null)
            {
                // The protocol is explicit that a failed probe is not fatal. Fall back to
                // whatever the manifest's patterns already said.
                if (candidate.Confidence > 0)
                {
                    return new ResolvedPlugin(descriptor, candidate.Confidence, candidate.Reason + " (probe unavailable)");
                }

                continue;
            }

            var confirmed = Routing.applyProbe(probe, candidate);

            if (confirmed.HasValue())
            {
                var value = confirmed!.Value;
                return new ResolvedPlugin(descriptor, value.Confidence, value.Reason);
            }

            logger.LogDebug("plugin {Plugin} declined {Url}", descriptor.Name, url);
        }

        return null;
    }

    /// <summary>
    /// Every plugin that might handle a URL, for the "no plugin claimed this" diagnostic.
    /// Pattern matching only — this is called to explain a failure, not to cause network
    /// traffic.
    /// </summary>
    public IReadOnlyList<string> CandidateNames(string url)
    {
        var compiled = registry.Usable.Select(descriptor => descriptor.Compiled!).ToFSharpList();
        return [.. Routing.candidates(compiled, url).AsList().Select(candidate => candidate.Plugin)];
    }
}
