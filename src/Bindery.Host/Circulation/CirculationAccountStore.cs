using System.Text.Json;
using Bindery.Core;
using Bindery.Host.Data;
using Bindery.Host.Plugins;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using CirculationCore = Bindery.Core.Circulation;

namespace Bindery.Host.Circulation;

public sealed record PickupLocationView(string Id, string Name);

public sealed record ConnectResult(CirculationAccountEntity? Account, string? Problem);

/// <summary>
/// People's library accounts, with their secrets encrypted at rest.
/// </summary>
/// <remarks>
/// <para>
/// Every method takes the owner and filters by it. There is deliberately no "find by id"
/// that does not: an account id in a URL is a guess anyone can make, and the owner check is
/// the only thing standing between one person and another's library card.
/// </para>
/// <para>
/// An account is only stored after the plugin's <c>verify</c> accepts it. That is both a
/// better experience — a typo is reported now, not as a sync failure later — and the reason
/// the library's own name can be shown instead of a select-box value.
/// </para>
/// </remarks>
public sealed class CirculationAccountStore(
    BinderyDbContext db,
    PluginRegistry registry,
    PluginClient client,
    IDataProtectionProvider protection,
    ILogger<CirculationAccountStore> logger)
{
    private readonly IDataProtector _protector = protection.CreateProtector("Bindery.CirculationAccounts.v1");

    /// <summary>Plugins a person can connect an account to.</summary>
    public IReadOnlyList<PluginDescriptor> CirculationPlugins =>
        [.. registry.Usable.Where(descriptor => descriptor.Manifest?.Circulation.HasValue() == true)];

    public async Task<IReadOnlyList<CirculationAccountEntity>> ListAsync(string owner, CancellationToken cancellationToken) =>
        await db.CirculationAccounts
            .Where(account => account.Owner == owner)
            .OrderBy(account => account.LibraryName)
            .ThenBy(account => account.CreatedAt)
            .ToListAsync(cancellationToken);

    public Task<CirculationAccountEntity?> FindAsync(string owner, Guid id, CancellationToken cancellationToken) =>
        db.CirculationAccounts.FirstOrDefaultAsync(account => account.Id == id && account.Owner == owner, cancellationToken);

    /// <summary>Connects a new account after the plugin has accepted its credentials.</summary>
    public async Task<ConnectResult> ConnectAsync(
        string owner,
        string plugin,
        IReadOnlyDictionary<string, string?> submitted,
        CancellationToken cancellationToken)
    {
        var (descriptor, spec) = Resolve(plugin);

        if (descriptor is null || spec is null)
        {
            return new ConnectResult(null, "That plugin does not offer library accounts.");
        }

        var problems = PluginSettingsStore.ValidateFields(spec.AccountFields, submitted, new HashSet<string>());

        if (problems.Count > 0)
        {
            return new ConnectResult(null, string.Join(" ", problems));
        }

        var entity = new CirculationAccountEntity { Owner = owner, Plugin = plugin };
        var fields = Normalize(spec, submitted, existing: null);

        var verified = await VerifyAsync(descriptor, owner, entity.Id, fields, cancellationToken);

        if (verified.Problem is not null)
        {
            return new ConnectResult(null, verified.Problem);
        }

        Apply(entity, spec, fields, verified.Value!);
        db.CirculationAccounts.Add(entity);
        await db.SaveChangesAsync(cancellationToken);

        // Never the card number: the display name is the plugin's non-secret description.
        logger.LogInformation("connected a {Plugin} account at {Library}", plugin, entity.LibraryName);
        return new ConnectResult(entity, null);
    }

    /// <summary>
    /// Re-enters credentials. An empty secret means "unchanged", exactly as plugin settings
    /// treat it, because the form cannot show the current value.
    /// </summary>
    public async Task<ConnectResult> UpdateAsync(
        string owner,
        Guid id,
        IReadOnlyDictionary<string, string?> submitted,
        CancellationToken cancellationToken)
    {
        var entity = await FindAsync(owner, id, cancellationToken);

        if (entity is null)
        {
            return new ConnectResult(null, "That library card is not connected.");
        }

        var (descriptor, spec) = Resolve(entity.Plugin);

        if (descriptor is null || spec is null)
        {
            return new ConnectResult(null, "The plugin for this card is not available right now.");
        }

        var existing = Fields(entity);
        var secretsSet = spec.AccountFields.AsList()
            .Where(field => field.Type.IsSecret && existing.ContainsKey(field.Key))
            .Select(field => field.Key)
            .ToHashSet(StringComparer.Ordinal);

        var problems = PluginSettingsStore.ValidateFields(spec.AccountFields, submitted, secretsSet);

        if (problems.Count > 0)
        {
            return new ConnectResult(null, string.Join(" ", problems));
        }

        var fields = Normalize(spec, submitted, existing);
        var verified = await VerifyAsync(descriptor, owner, entity.Id, fields, cancellationToken);

        if (verified.Problem is not null)
        {
            return new ConnectResult(null, verified.Problem);
        }

        Apply(entity, spec, fields, verified.Value!);
        entity.NeedsAttention = false;
        entity.LastError = null;
        await db.SaveChangesAsync(cancellationToken);

        return new ConnectResult(entity, null);
    }

    public async Task<bool> RemoveAsync(string owner, Guid id, CancellationToken cancellationToken)
    {
        var entity = await FindAsync(owner, id, cancellationToken);

        if (entity is null)
        {
            return false;
        }

        db.CirculationAccounts.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>The account as the protocol sends it, secrets decrypted.</summary>
    public CirculationCore.Account ToProtocol(CirculationAccountEntity entity) =>
        new(entity.Id.ToString("D"), Fields(entity).ToFSharpMap());

    /// <summary>The non-secret field values, for prefilling an edit form.</summary>
    public static IReadOnlyDictionary<string, string> PlainFields(CirculationAccountEntity entity) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(entity.FieldsJson) ?? new Dictionary<string, string>();

    public static IReadOnlyList<PickupLocationView> PickupLocations(CirculationAccountEntity entity) =>
        JsonSerializer.Deserialize<List<PickupLocationView>>(entity.PickupLocationsJson) ?? [];

    public (PluginDescriptor? Descriptor, Protocol.CirculationSpec? Spec) Resolve(string plugin)
    {
        var descriptor = registry.Find(plugin);

        return descriptor is { IsUsable: true, Manifest: not null }
            ? (descriptor, descriptor.Manifest.Circulation.OrNull())
            : (null, null);
    }

    // ------------------------------------------------------------ internals

    private Dictionary<string, string> Fields(CirculationAccountEntity entity)
    {
        var fields = new Dictionary<string, string>(PlainFields(entity), StringComparer.Ordinal);

        if (string.IsNullOrEmpty(entity.ProtectedSecrets))
        {
            return fields;
        }

        try
        {
            var secrets = JsonSerializer.Deserialize<Dictionary<string, string>>(_protector.Unprotect(entity.ProtectedSecrets));

            foreach (var (key, value) in secrets ?? [])
            {
                fields[key] = value;
            }
        }
        catch (Exception ex)
        {
            // Lost data-protection keys make secrets unrecoverable; the person re-enters them.
            logger.LogError(ex, "could not decrypt the secrets of circulation account {Account}", entity.Id);
        }

        return fields;
    }

    private static Dictionary<string, string> Normalize(
        Protocol.CirculationSpec spec,
        IReadOnlyDictionary<string, string?> submitted,
        IReadOnlyDictionary<string, string>? existing)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var field in spec.AccountFields.AsList())
        {
            var value = submitted.TryGetValue(field.Key, out var raw) ? (raw ?? string.Empty).Trim() : string.Empty;

            if (value.Length == 0 && field.Type.IsSecret && existing?.TryGetValue(field.Key, out var kept) == true)
            {
                value = kept;
            }

            if (value.Length > 0)
            {
                fields[field.Key] = value;
            }
        }

        return fields;
    }

    private async Task<(CirculationCore.Verified? Value, string? Problem)> VerifyAsync(
        PluginDescriptor descriptor,
        string owner,
        Guid accountId,
        IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken)
    {
        try
        {
            var reply = await client.VerifyAccountAsync(
                descriptor.Entry, owner, new CirculationCore.Account(accountId.ToString("D"), fields.ToFSharpMap()), cancellationToken);

            return reply switch
            {
                CirculationCore.Reply<CirculationCore.Verified>.Answered answered => (answered.Item, null),
                CirculationCore.Reply<CirculationCore.Verified>.Refused refused =>
                    (null, refused.Item.Code.IsAuthRequired
                        ? "The library did not accept that card number and PIN."
                        : refused.Item.Message),
                _ => (null, "The plugin answered with something unrecognizable.")
            };
        }
        catch (Exception ex) when (ex is PluginTransportException or HttpRequestException or OperationCanceledException
                                       && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "verify on {Plugin} failed", descriptor.Name);
            return (null, $"{descriptor.DisplayName} could not reach the library. Try again in a moment.");
        }
    }

    private void Apply(
        CirculationAccountEntity entity,
        Protocol.CirculationSpec spec,
        IReadOnlyDictionary<string, string> fields,
        CirculationCore.Verified verified)
    {
        var secretKeys = spec.AccountFields.AsList().Where(f => f.Type.IsSecret).Select(f => f.Key).ToHashSet(StringComparer.Ordinal);

        var plain = fields.Where(pair => !secretKeys.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);
        var secret = fields.Where(pair => secretKeys.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);

        entity.FieldsJson = JsonSerializer.Serialize(plain);
        entity.ProtectedSecrets = secret.Count == 0 ? string.Empty : _protector.Protect(JsonSerializer.Serialize(secret));
        entity.LibraryName = Clip(verified.LibraryName, 200);
        entity.DisplayName = verified.DisplayName.OrNull() is { } name ? Clip(name, 200) : null;
        entity.PickupLocationsJson = JsonSerializer.Serialize(
            verified.PickupLocations.AsList().Select(location => new PickupLocationView(location.Id, location.Name)));
    }

    private static string Clip(string value, int limit) => value.Length <= limit ? value : value[..limit];
}
