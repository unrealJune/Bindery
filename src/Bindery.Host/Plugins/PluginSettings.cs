using Bindery.Core;
using Bindery.Host.Data;
using Bindery.Host.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Bindery.Host.Plugins;

/// <summary>
/// Per-plugin configuration, with secrets encrypted at rest.
/// </summary>
/// <remarks>
/// <para>
/// Values are stored as strings because that is how they travel on the wire — the manifest
/// says what each one means. Secret values are encrypted with Data Protection, never
/// logged, and never returned to the UI in cleartext; the UI is told only whether one is
/// set.
/// </para>
/// <para>
/// Two kinds of value, by the field's manifest <c>scope</c> (protocol §4.1). An install
/// value has no owner and is the admin's to set. A user value belongs to one person, and
/// nothing here returns it to anyone else — every method that touches one takes the owner.
/// </para>
/// </remarks>
public sealed class PluginSettingsStore(
    BinderyDbContext db,
    IDataProtectionProvider protection,
    UserDirectory users,
    ILogger<PluginSettingsStore> logger)
{
    private readonly IDataProtector _protector = protection.CreateProtector("Bindery.PluginSettings.v1");

    /// <summary>
    /// The <c>config</c> map to send a plugin, secrets decrypted: install values, with
    /// <paramref name="actingFor"/>'s own user values over the top. With nobody acting, install
    /// values only — a user-scoped key is never filled in from someone else's.
    /// </summary>
    public async Task<Dictionary<string, string>> GetForPluginAsync(
        Protocol.Manifest manifest,
        string? actingFor,
        CancellationToken cancellationToken)
    {
        await ClaimLegacyUserValuesAsync(manifest, cancellationToken);

        var userKeys = KeysOf(manifest.UserConfig);

        var rows = await db.PluginSettings
            .AsNoTracking()
            .Where(setting => setting.Plugin == manifest.Name
                && (setting.Owner == null || (actingFor != null && setting.Owner == actingFor)))
            .ToListAsync(cancellationToken);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        // Install first, then the person's: the overlay order §4.1 requires. A key the
        // manifest no longer declares at all still goes as an install value, as it always has.
        foreach (var row in rows.Where(row => row.Owner is null && !userKeys.Contains(row.Key)))
        {
            Put(values, manifest.Name, row);
        }

        foreach (var row in rows.Where(row => row.Owner is not null && userKeys.Contains(row.Key)))
        {
            Put(values, manifest.Name, row);
        }

        return values;
    }

    /// <summary>
    /// What the UI may see of one section: the install values when <paramref name="owner"/>
    /// is null, otherwise that person's own. Plain values, plus which secrets exist.
    /// </summary>
    public async Task<PluginSettingsView> GetForDisplayAsync(
        Protocol.Manifest manifest,
        string? owner,
        CancellationToken cancellationToken)
    {
        await ClaimLegacyUserValuesAsync(manifest, cancellationToken);

        var keys = KeysOf(owner is null ? manifest.InstallConfig : manifest.UserConfig);

        var rows = await db.PluginSettings
            .Where(setting => setting.Plugin == manifest.Name && setting.Owner == owner)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var plain = new Dictionary<string, string>(StringComparer.Ordinal);
        var secretsSet = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in rows.Where(row => keys.Contains(row.Key)))
        {
            if (row.IsSecret)
            {
                secretsSet.Add(row.Key);
            }
            else
            {
                plain[row.Key] = row.Value;
            }
        }

        return new PluginSettingsView(plain, secretsSet);
    }

    /// <summary>
    /// Applies a form submission to one section: the install fields when
    /// <paramref name="owner"/> is null, otherwise that person's user fields. A key from
    /// the other section is ignored, whatever the form claimed.
    /// </summary>
    /// <remarks>
    /// A secret whose submitted value is empty is left alone rather than cleared: the UI
    /// cannot show the current value, so an empty box means "unchanged", not "delete".
    /// Clearing is an explicit action.
    /// </remarks>
    public async Task SaveAsync(
        Protocol.Manifest manifest,
        string? owner,
        IReadOnlyDictionary<string, string?> submitted,
        CancellationToken cancellationToken)
    {
        var existing = await db.PluginSettings
            .Where(setting => setting.Plugin == manifest.Name && setting.Owner == owner)
            .ToListAsync(cancellationToken);

        foreach (var field in FieldsFor(manifest, owner))
        {
            if (!submitted.TryGetValue(field.Key, out var raw))
            {
                continue;
            }

            var row = existing.FirstOrDefault(setting => setting.Key == field.Key);
            var isSecret = field.Type.IsSecret;
            var value = raw ?? string.Empty;

            if (isSecret && string.IsNullOrEmpty(value))
            {
                continue;
            }

            if (!isSecret && string.IsNullOrEmpty(value))
            {
                if (row is not null)
                {
                    db.PluginSettings.Remove(row);
                }

                continue;
            }

            var stored = isSecret ? _protector.Protect(value) : value;

            if (row is null)
            {
                db.PluginSettings.Add(new PluginSettingEntity
                {
                    Plugin = manifest.Name,
                    Owner = owner,
                    Key = field.Key,
                    Value = stored,
                    IsSecret = isSecret
                });
            }
            else
            {
                row.Value = stored;
                row.IsSecret = isSecret;
                row.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "updated {Scope} settings for plugin {Plugin}", owner is null ? "install" : "personal", manifest.Name);
    }

    /// <summary>Clears one value: the install one when <paramref name="owner"/> is null, else theirs.</summary>
    public async Task ClearAsync(string plugin, string key, string? owner, CancellationToken cancellationToken)
    {
        var rows = await db.PluginSettings
            .Where(setting => setting.Plugin == plugin && setting.Key == key && setting.Owner == owner)
            .ToListAsync(cancellationToken);

        db.PluginSettings.RemoveRange(rows);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The fields one section of the form edits.</summary>
    public static IReadOnlyList<Protocol.ConfigField> FieldsFor(Protocol.Manifest manifest, string? owner) =>
        (owner is null ? manifest.InstallConfig : manifest.UserConfig).AsList();

    /// <summary>
    /// Hands an install value for a field the plugin now declares <c>user</c> to the founder.
    /// </summary>
    /// <remarks>
    /// Before scopes, one person's login was everyone's. When a plugin starts marking that
    /// field personal, the value it already holds was almost certainly the founder's — the
    /// person who set Bindery up — so it becomes theirs rather than being sent to everybody
    /// or silently lost. Until anyone has signed in there is no founder, and the value waits
    /// (unsent: an install row for a user-scoped key never goes on the wire).
    /// </remarks>
    private async Task ClaimLegacyUserValuesAsync(Protocol.Manifest manifest, CancellationToken cancellationToken)
    {
        var userKeys = KeysOf(manifest.UserConfig);

        if (userKeys.Count == 0)
        {
            return;
        }

        var personal = userKeys.ToArray();

        var legacy = await db.PluginSettings
            .Where(setting => setting.Plugin == manifest.Name && setting.Owner == null && personal.Contains(setting.Key))
            .ToListAsync(cancellationToken);

        if (legacy.Count == 0)
        {
            return;
        }

        var founder = await users.FounderAsync(cancellationToken);

        if (founder is null)
        {
            return;
        }

        var theirs = await db.PluginSettings
            .Where(setting => setting.Plugin == manifest.Name && setting.Owner == founder)
            .Select(setting => setting.Key)
            .ToListAsync(cancellationToken);

        foreach (var row in legacy)
        {
            if (theirs.Contains(row.Key))
            {
                // They already set their own; the old shared value is simply retired.
                db.PluginSettings.Remove(row);
            }
            else
            {
                row.Owner = founder;
                row.UpdatedAt = DateTimeOffset.UtcNow;
            }

            logger.LogInformation("{Plugin}.{Key} is now personal; the shared value went to the founder", manifest.Name, row.Key);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // A concurrent request did the same move first. Its result stands.
            logger.LogDebug(ex, "legacy settings for {Plugin} were claimed concurrently", manifest.Name);

            foreach (var entry in db.ChangeTracker.Entries<PluginSettingEntity>().ToList())
            {
                entry.State = EntityState.Detached;
            }
        }
    }

    private void Put(Dictionary<string, string> values, string plugin, PluginSettingEntity row)
    {
        if (!row.IsSecret)
        {
            values[row.Key] = row.Value;
            return;
        }

        try
        {
            values[row.Key] = _protector.Unprotect(row.Value);
        }
        catch (Exception ex)
        {
            // Losing the data-protection keys makes secrets unrecoverable. That is the
            // correct outcome; the owner re-enters them. Say which key, never what.
            logger.LogError(ex, "could not decrypt {Plugin}.{Key}; it must be set again", plugin, row.Key);
        }
    }

    private static HashSet<string> KeysOf(Microsoft.FSharp.Collections.FSharpList<Protocol.ConfigField> fields) =>
        fields.AsList().Select(field => field.Key).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Checks a submission against the manifest's own schema before it is stored.
    /// </summary>
    public static IReadOnlyList<string> Validate(
        Protocol.Manifest manifest,
        string? owner,
        IReadOnlyDictionary<string, string?> submitted,
        IReadOnlySet<string> alreadySetSecrets) =>
        ValidateFields(owner is null ? manifest.InstallConfig : manifest.UserConfig, submitted, alreadySetSecrets);

    /// <summary>
    /// The same checks for any list of §4 field descriptors — plugin settings, action input,
    /// and circulation account fields all share one schema, so they share one validator.
    /// </summary>
    public static IReadOnlyList<string> ValidateFields(
        Microsoft.FSharp.Collections.FSharpList<Protocol.ConfigField> fields,
        IReadOnlyDictionary<string, string?> submitted,
        IReadOnlySet<string> alreadySetSecrets)
    {
        var problems = new List<string>();

        foreach (var field in fields.AsList())
        {
            if (!submitted.TryGetValue(field.Key, out var raw))
            {
                continue;
            }

            var value = (raw ?? string.Empty).Trim();

            if (value.Length == 0)
            {
                if (field.Required && !(field.Type.IsSecret && alreadySetSecrets.Contains(field.Key)))
                {
                    problems.Add($"{field.Label} is required.");
                }

                continue;
            }

            if (field.Type.IsSelectField)
            {
                var allowed = field.Options.AsList().Select(option => option.Value).ToHashSet(StringComparer.Ordinal);

                if (!allowed.Contains(value))
                {
                    problems.Add($"{field.Label} must be one of: {string.Join(", ", allowed)}.");
                }
            }

            if (field.Type.IsIntField)
            {
                if (!int.TryParse(value, out var number))
                {
                    problems.Add($"{field.Label} must be a whole number.");
                }
                else
                {
                    var min = field.Min.OrNullable();
                    var max = field.Max.OrNullable();

                    if (min is not null && number < min)
                    {
                        problems.Add($"{field.Label} must be at least {min}.");
                    }

                    if (max is not null && number > max)
                    {
                        problems.Add($"{field.Label} must be at most {max}.");
                    }
                }
            }

            if (field.Type.IsUrlField && !Uri.TryCreate(value, UriKind.Absolute, out _))
            {
                problems.Add($"{field.Label} must be an absolute URL.");
            }

            var pattern = field.Pattern.OrNull();

            if (pattern is not null)
            {
                try
                {
                    var regex = new System.Text.RegularExpressions.Regex(
                        pattern,
                        System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(100));

                    if (!regex.IsMatch(value))
                    {
                        problems.Add($"{field.Label} is not in the expected format.");
                    }
                }
                catch (Exception ex) when (ex is ArgumentException
                                               or System.Text.RegularExpressions.RegexMatchTimeoutException)
                {
                    // A plugin's bad pattern must not block a user from saving.
                }
            }
        }

        return problems;
    }
}

public sealed record PluginSettingsView(
    IReadOnlyDictionary<string, string> Values,
    IReadOnlySet<string> SecretsSet)
{
    public string ValueFor(string key) => Values.TryGetValue(key, out var value) ? value : string.Empty;

    public bool IsSecretSet(string key) => SecretsSet.Contains(key);
}
