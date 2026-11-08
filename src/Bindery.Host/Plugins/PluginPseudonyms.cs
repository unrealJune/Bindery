using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Bindery.Host.Configuration;
using Bindery.Host.Security;
using Microsoft.Extensions.Options;

namespace Bindery.Host.Plugins;

/// <summary>The headers that tell a plugin who it is serving (protocol §2.3).</summary>
public static class PersonHeaders
{
    public const string User = "X-Bindery-User";
    public const string Role = "X-Bindery-Role";
}

/// <summary>
/// The opaque per-plugin name a person goes by in <c>X-Bindery-User</c>.
/// </summary>
/// <remarks>
/// <para>
/// HMAC-SHA256 of the person's subject under a key derived for that one plugin from a master
/// key on the data volume. The identity provider's subject never leaves the host, two plugins
/// cannot correlate their users, and the value survives restarts — which is what lets a
/// plugin key state on it at all.
/// </para>
/// <para>
/// The master key lives beside the database rather than in it because it is not rebuildable:
/// lose it and every plugin sees every person as a stranger. A key file of the wrong size is
/// refused rather than replaced, for the same reason.
/// </para>
/// </remarks>
public sealed class PluginPseudonyms(IOptions<BinderyOptions> options, ILogger<PluginPseudonyms> logger)
{
    public const string KeyFileName = "pseudonym.key";
    private const int KeyBytes = 32;

    private readonly ConcurrentDictionary<string, byte[]> _pluginKeys = new(StringComparer.Ordinal);
    private readonly object _loadLock = new();
    private byte[]? _master;

    /// <summary>The pseudonym <paramref name="subject"/> goes by for <paramref name="plugin"/>.</summary>
    public string For(string plugin, string subject)
    {
        var key = _pluginKeys.GetOrAdd(
            plugin,
            name => HMACSHA256.HashData(Master(), Encoding.UTF8.GetBytes("bindery:plugin-user:v1:" + name)));

        return Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(subject))).ToLowerInvariant();
    }

    /// <summary>
    /// Adds the §2.3 headers for <paramref name="person"/>, or nothing when no person is
    /// acting. Any value already on the message is replaced, never appended to.
    /// </summary>
    public void Apply(HttpRequestMessage message, string plugin, Person? person)
    {
        message.Headers.Remove(PersonHeaders.User);
        message.Headers.Remove(PersonHeaders.Role);

        if (person is null || string.IsNullOrEmpty(person.Subject))
        {
            return;
        }

        message.Headers.Add(PersonHeaders.User, For(plugin, person.Subject));
        message.Headers.Add(PersonHeaders.Role, person.IsAdmin ? BinderyClaims.AdminRole : BinderyClaims.MemberRole);
    }

    private byte[] Master()
    {
        if (_master is { } loaded)
        {
            return loaded;
        }

        lock (_loadLock)
        {
            return _master ??= LoadOrCreate(Path.Combine(options.Value.DataPath, KeyFileName));
        }
    }

    private byte[] LoadOrCreate(string path)
    {
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            try
            {
                using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                file.Write(RandomNumberGenerator.GetBytes(KeyBytes));

                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                logger.LogInformation("created the plugin pseudonym key at {Path}", path);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another process won the race; its key is the key.
            }
        }

        var key = File.ReadAllBytes(path);

        if (key.Length != KeyBytes)
        {
            throw new InvalidOperationException(
                $"{path} is {key.Length} bytes, not {KeyBytes}. It is not regenerated automatically: a new key "
                + "would make every plugin forget every person. Restore it from backup, or delete it to start over.");
        }

        return key;
    }
}
