using Bindery.Core;
using Bindery.Host.Security;

namespace Bindery.Host.Plugins;

/// <summary>A rendered action result, ready for the generic UI to display.</summary>
public sealed record ActionResult(
    bool Succeeded,
    string? Error,
    string Kind,
    IReadOnlyList<ActionListItem> Items,
    string? Text,
    string? Message,
    string? Level);

public sealed record ActionListItem(string Title, string? Subtitle, string? Url, string? Thumbnail);

/// <summary>
/// Runs a plugin's declared actions.
/// </summary>
/// <remarks>
/// Actions are what stop tier 1 from meaning "settings only". A plugin declares an
/// operation with typed input and output; Bindery renders the form, calls it, and renders
/// the result. "Search a site and pick a result" costs the plugin author a manifest entry
/// and no HTML at all — which is why the fragment tier stays for the cases that genuinely
/// need it rather than becoming the default.
/// <para>
/// An action runs as the person who invoked it: their user-scoped config, their identity
/// headers (§2.3). One declared <c>access: admin</c> is refused to anyone else here, before
/// the plugin is ever called.
/// </para>
/// </remarks>
public sealed class PluginActionService(
    PluginRegistry registry,
    PluginClient client,
    PluginSettingsStore settings,
    ILogger<PluginActionService> logger)
{
    public Protocol.PluginAction? FindAction(string plugin, string action)
    {
        var descriptor = registry.Find(plugin);

        return descriptor?.Manifest?.Actions
            .AsList()
            .FirstOrDefault(candidate => candidate.Name == action);
    }

    /// <summary>The actions <paramref name="person"/> may run, in manifest order.</summary>
    public static IReadOnlyList<Protocol.PluginAction> AvailableTo(Protocol.Manifest manifest, Person person) =>
        [.. manifest.Actions.AsList().Where(action => action.Access.Permits(person.IsAdmin))];

    public async Task<ActionResult> InvokeAsync(
        string plugin,
        string action,
        IReadOnlyDictionary<string, string> input,
        Person person,
        CancellationToken cancellationToken)
    {
        var descriptor = registry.Find(plugin);

        if (descriptor is not { IsUsable: true } || descriptor.Manifest is null)
        {
            return Failure("That plugin is not available.");
        }

        var declared = FindAction(plugin, action);

        if (declared is null)
        {
            return Failure("That plugin does not offer this action.");
        }

        if (!declared.Access.Permits(person.IsAdmin))
        {
            return Failure("Only an admin can run this action.");
        }

        var problems = ValidateInput(declared, input);

        if (problems.Count > 0)
        {
            return Failure(string.Join(" ", problems));
        }

        var config = await settings.GetForPluginAsync(descriptor.Manifest, person.Subject, cancellationToken);

        try
        {
            var outcome = await client.InvokeActionAsync(descriptor.Entry, action, input, config, person, cancellationToken);

            return outcome switch
            {
                Protocol.ActionOutcome.ItemsResult items => new ActionResult(
                    true,
                    null,
                    "list",
                    [
                        .. items.items.AsList().Select(item => new ActionListItem(
                            item.Title,
                            item.Subtitle.OrNull(),
                            item.Url.OrNull(),
                            item.Thumbnail.OrNull()))
                    ],
                    null,
                    null,
                    null),

                Protocol.ActionOutcome.TextResult text =>
                    new ActionResult(true, null, "text", [], text.text, null, null),

                Protocol.ActionOutcome.MessageResult message =>
                    new ActionResult(true, null, "message", [], null, message.message, message.level.Wire),

                Protocol.ActionOutcome.ActionFailed failure =>
                    Failure($"{failure.error.Message}"),

                _ => Failure("The plugin returned something unrecognizable.")
            };
        }
        catch (PluginTransportException ex)
        {
            logger.LogWarning(ex, "action {Action} on {Plugin} failed", action, plugin);
            return Failure(ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException
                                       && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "action {Action} on {Plugin} did not answer", action, plugin);
            return Failure($"{descriptor.DisplayName} is not responding.");
        }
    }

    private static IReadOnlyList<string> ValidateInput(
        Protocol.PluginAction action,
        IReadOnlyDictionary<string, string> input)
    {
        var submitted = action.Input
            .AsList()
            .ToDictionary(field => field.Key, field => input.GetValueOrDefault(field.Key));

        // The same validator the settings form uses: an action's input is declared with
        // exactly the same field schema, so it gets exactly the same checks.
        return PluginSettingsStore.ValidateFields(action.Input, submitted!, new HashSet<string>());
    }

    private static ActionResult Failure(string message) =>
        new(false, message, "message", [], null, null, "error");
}
