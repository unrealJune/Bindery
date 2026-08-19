using Bindery.Core;
using Bindery.Host.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Bindery.Host.Plugins;

/// <summary>
/// Per-plugin configuration, with secrets encrypted at rest.
/// </summary>
/// <remarks>
/// Values are stored as strings because that is how they travel on the wire — the manifest
/// says what each one means. Secret values are encrypted with Data Protection, never
/// logged, and never returned to the UI in cleartext; the UI is told only whether one is
/// set.
/// </remarks>
public sealed class PluginSettingsStore(
    BinderyDbContext db,
    IDataProtectionProvider protection,
    ILogger<PluginSettingsStore> logger)
{
    private readonly IDataProtector _protector = protection.CreateProtector("Bindery.PluginSettings.v1");

    /// <summary>
    /// Every configured value for a plugin, secrets decrypted, ready to send to it.
    /// </summary>
    public async Task<Dictionary<string, string>> GetForPluginAsync(
        string plugin,
        CancellationToken cancellationToken)
    {
        var rows = await db.PluginSettings
            .Where(setting => setting.Plugin == plugin)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (!row.IsSecret)
            {
                values[row.Key] = row.Value;
                continue;
            }

            try
            {
                values[row.Key] = _protector.Unprotect(row.Value);
            }
            catch (Exception ex)
            {
                // Losing the data-protection keys makes secrets unrecoverable. That is the
                // correct outcome; the operator re-enters them. Say which key, never what.
                logger.LogError(ex, "could not decrypt {Plugin}.{Key}; it must be set again", plugin, row.Key);
            }
        }

        return values;
    }

    /// <summary>What the UI may see: plain values, plus which secrets exist.</summary>
    public async Task<PluginSettingsView> GetForDisplayAsync(
        Protocol.Manifest manifest,
        CancellationToken cancellationToken)
    {
        var rows = await db.PluginSettings
            .Where(setting => setting.Plugin == manifest.Name)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var plain = new Dictionary<string, string>(StringComparer.Ordinal);
        var secretsSet = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in rows)
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
    /// Applies a form submission.
    /// </summary>
    /// <remarks>
    /// A secret whose submitted value is empty is left alone rather than cleared: the UI
    /// cannot show the current value, so an empty box means "unchanged", not "delete".
    /// Clearing is an explicit action.
    /// </remarks>
    public async Task SaveAsync(
        Protocol.Manifest manifest,
        IReadOnlyDictionary<string, string?> submitted,
        CancellationToken cancellationToken)
    {
        var existing = await db.PluginSettings
            .Where(setting => setting.Plugin == manifest.Name)
            .ToListAsync(cancellationToken);

        foreach (var field in manifest.Config.AsList())
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
        logger.LogInformation("updated settings for plugin {Plugin}", manifest.Name);
    }

    public async Task ClearAsync(string plugin, string key, CancellationToken cancellationToken)
    {
        var rows = await db.PluginSettings
            .Where(setting => setting.Plugin == plugin && setting.Key == key)
            .ToListAsync(cancellationToken);

        db.PluginSettings.RemoveRange(rows);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Checks a submission against the manifest's own schema before it is stored.
    /// </summary>
    public static IReadOnlyList<string> Validate(
        Protocol.Manifest manifest,
        IReadOnlyDictionary<string, string?> submitted,
        IReadOnlySet<string> alreadySetSecrets)
    {
        var problems = new List<string>();

        foreach (var field in manifest.Config.AsList())
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
