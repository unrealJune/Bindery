using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Bindery.Host.Plugins;

/// <summary>
/// Issues and checks the per-plugin bearer tokens for protocol §3.7 change notifications.
/// </summary>
/// <remarks>
/// This is the only credential that travels host → plugin for the plugin to use *back*, so
/// it is deliberately not the shared plugin token: that one authenticates Bindery to a
/// plugin, and reusing it would mean a plugin could impersonate Bindery to its siblings in
/// the same pod. One token per plugin, minted in memory at first use, means a leak is scoped
/// to that plugin's own books and dies with the process — there is nothing at rest to steal
/// and nothing to rotate by hand.
///
/// Identity always comes from the token, never from the caller's claim about which plugin it
/// is. <see cref="Resolve"/> returns the name the token was issued to; the request body's
/// `plugin` field is only ever compared against that.
/// </remarks>
public sealed class PluginNotifyTokens
{
    private readonly ConcurrentDictionary<string, string> _byPlugin = new(StringComparer.Ordinal);

    /// <summary>The token for a plugin, minted on first request and stable thereafter.</summary>
    public string For(string pluginName) =>
        _byPlugin.GetOrAdd(pluginName, static _ => RandomNumberGenerator.GetHexString(64, lowercase: true));

    /// <summary>
    /// The plugin a presented token belongs to, or null. Compared in constant time against
    /// every issued token — the repo's rule for feed tokens, and the same reasoning applies
    /// here: a length-sensitive or early-exit compare leaks the token a byte at a time.
    /// </summary>
    public string? Resolve(string? presented)
    {
        if (string.IsNullOrEmpty(presented))
        {
            return null;
        }

        var presentedBytes = System.Text.Encoding.UTF8.GetBytes(presented);
        string? matched = null;

        // Every candidate is examined even after a hit, so the work done does not depend on
        // which plugin matched or on how early it sits in the dictionary.
        foreach (var (plugin, token) in _byPlugin)
        {
            var candidate = System.Text.Encoding.UTF8.GetBytes(token);

            if (CryptographicOperations.FixedTimeEquals(presentedBytes, candidate))
            {
                matched = plugin;
            }
        }

        return matched;
    }
}
