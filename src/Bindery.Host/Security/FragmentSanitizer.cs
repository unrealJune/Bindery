using System.Text.RegularExpressions;
using Ganss.Xss;

namespace Bindery.Host.Security;

/// <summary>
/// Sanitizes HTML fragments served by plugins before they are rendered in Bindery's origin.
/// </summary>
/// <remarks>
/// <para>
/// This is the security-critical class in the codebase. Plugin HTML rendered same-origin is
/// stored XSS by construction: a downloader that returns EPUBs never touches a session
/// cookie, but markup injected into Bindery's origin does. Three layers are required and
/// all three are load-bearing — this allowlist, a CSP with no <c>unsafe-inline</c>
/// (<see cref="SecurityHeaders"/>), and antiforgery tokens injected by the proxy wrapper.
/// </para>
/// <para>
/// The rules that matter most, in the order they break things:
/// </para>
/// <list type="number">
/// <item>No JavaScript reaches the page. Not <c>&lt;script&gt;</c>, not <c>on*</c>, not
/// <c>javascript:</c>, and — the one people miss — not htmx's own <c>hx-on</c> or the
/// <c>js:</c> prefix on <c>hx-vals</c>/<c>hx-headers</c>, which are inline JavaScript
/// wearing an attribute that looks declarative.</item>
/// <item>Every URL stays on this origin, under the plugin's own mount point. A plugin
/// cannot make the browser talk to a host of its choosing with the user's cookies.</item>
/// <item>Nothing may address Bindery's own DOM. Ids and names in the reserved
/// <c>bnd-</c> namespace are dropped, and <c>hx-swap-oob</c> — which would let a fragment
/// replace any element on the page — is refused outright.</item>
/// </list>
/// <para>
/// If this ever feels inconvenient, the answer is <c>ui.mode: "iframe"</c>, not a loosened
/// allowlist. Every entry below is covered by a vector in
/// <c>tests/conformance/vectors/xss.json</c>; adding one without adding a vector is how
/// that merge gate quietly stops working.
/// </para>
/// </remarks>
public sealed class FragmentSanitizer
{
    /// <summary>Ids and names in this namespace belong to Bindery's shell.</summary>
    public const string ReservedIdPrefix = "bnd-";

    private static readonly string[] AllowedTags =
    [
        // structure
        "div", "span", "p", "section", "article", "aside", "nav", "main", "hgroup",
        "h1", "h2", "h3", "h4", "h5", "h6", "hr", "br", "wbr",
        // text
        "a", "abbr", "b", "bdi", "bdo", "blockquote", "cite", "code", "del", "dfn", "em",
        "i", "ins", "kbd", "mark", "pre", "q", "s", "samp", "small", "strong", "sub",
        "sup", "time", "u", "var",
        // lists
        "ul", "ol", "li", "dl", "dt", "dd",
        // tables
        "table", "caption", "colgroup", "col", "thead", "tbody", "tfoot", "tr", "th", "td",
        // forms
        "form", "fieldset", "legend", "label", "input", "button", "select", "option",
        "optgroup", "textarea", "output", "progress", "meter", "datalist",
        // media and disclosure
        "img", "figure", "figcaption", "details", "summary"
    ];

    private static readonly string[] AllowedAttributes =
    [
        // `id`, `name`, `for`, `form`, and `list` are deliberately absent: they are
        // re-admitted in IsPermittedExtraAttribute so their values can be checked against
        // the reserved namespace first.
        "class", "title", "lang", "dir", "role",
        "href", "src", "alt", "width", "height", "loading", "decoding",
        "value", "type", "placeholder",
        "checked", "selected", "disabled", "readonly", "required", "multiple",
        "min", "max", "step", "minlength", "maxlength", "pattern", "size",
        "rows", "cols", "wrap", "spellcheck", "autocomplete", "inputmode",
        "colspan", "rowspan", "headers", "scope", "span",
        "open", "start", "reversed", "datetime", "cite", "label", "style"
    ];

    /// <summary>Layout only. Colour and typography are Bindery's, via the design tokens.</summary>
    private static readonly string[] AllowedCssProperties =
    [
        "display", "flex", "flex-basis", "flex-direction", "flex-grow", "flex-shrink",
        "flex-wrap", "gap", "row-gap", "column-gap", "align-items", "align-self",
        "justify-content", "justify-self", "order",
        "grid-template-columns", "grid-template-rows", "grid-column", "grid-row",
        "width", "min-width", "max-width", "height", "min-height", "max-height",
        "margin", "margin-top", "margin-right", "margin-bottom", "margin-left",
        "padding", "padding-top", "padding-right", "padding-bottom", "padding-left",
        "text-align", "vertical-align", "white-space", "word-break", "overflow-wrap"
    ];

    /// <summary>
    /// htmx attributes a plugin may use. An allowlist rather than a "hx-* minus a few"
    /// rule: htmx adds attributes, and a new one that turns out to execute strings should
    /// not become permitted by default the day someone upgrades the vendored runtime.
    /// </summary>
    private static readonly HashSet<string> AllowedHxAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "hx-get", "hx-post", "hx-put", "hx-patch", "hx-delete",
        "hx-target", "hx-swap", "hx-trigger", "hx-indicator", "hx-select",
        "hx-push-url", "hx-confirm", "hx-disable", "hx-disabled-elt", "hx-encoding",
        "hx-ext", "hx-history-elt", "hx-include", "hx-params", "hx-preserve",
        "hx-prompt", "hx-sync", "hx-validate", "hx-boost", "hx-sse", "sse-connect", "sse-swap"
    };

    private static readonly HashSet<string> UrlAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "href", "src", "action", "formaction",
        "hx-get", "hx-post", "hx-put", "hx-patch", "hx-delete"
    };

    /// <summary>Attributes whose value names a DOM element and so must not reach Bindery's.</summary>
    private static readonly HashSet<string> IdentifierAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "name", "for", "form", "list"
    };

    private static readonly Regex AriaAttribute = new("^aria-[a-z-]+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ImageDataUri = new(
        @"^data:image/(png|jpeg|jpg|gif|webp);base64,[A-Za-z0-9+/=\s]+$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ILogger<FragmentSanitizer> _logger;

    public FragmentSanitizer(ILogger<FragmentSanitizer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Cleans a fragment for a plugin mounted at <paramref name="basePath"/>.
    /// </summary>
    /// <returns>The sanitized markup and a note of anything that had to be removed.</returns>
    public SanitizedFragment Sanitize(string html, string pluginName, string basePath)
    {
        var removals = new List<string>();
        var sanitizer = CreateSanitizer(basePath, removals);
        var clean = sanitizer.Sanitize(html);

        if (removals.Count > 0)
        {
            // A plugin whose markup is being stripped is a plugin that will look broken to
            // its users, so this is warned about rather than silently swallowed.
            _logger.LogWarning(
                "sanitized {Count} disallowed item(s) from a {Plugin} fragment: {Items}",
                removals.Count,
                pluginName,
                string.Join(", ", removals.Distinct().Take(10)));
        }

        return new SanitizedFragment(clean, removals);
    }

    private HtmlSanitizer CreateSanitizer(string basePath, List<string> removals)
    {
        // A fresh instance per call: the event handlers close over this request's base path
        // and removal list, and a shared instance would leak one plugin's rules into
        // another's fragment under concurrency.
        var sanitizer = new HtmlSanitizer();

        sanitizer.AllowedTags.Clear();
        sanitizer.AllowedTags.UnionWith(AllowedTags);

        sanitizer.AllowedAttributes.Clear();
        sanitizer.AllowedAttributes.UnionWith(AllowedAttributes);

        sanitizer.AllowedCssProperties.Clear();
        sanitizer.AllowedCssProperties.UnionWith(AllowedCssProperties);

        sanitizer.AllowedSchemes.Clear();
        sanitizer.AllowedSchemes.Add("https");

        sanitizer.AllowedAtRules.Clear();
        sanitizer.AllowDataAttributes = true;
        sanitizer.KeepChildNodes = false;

        sanitizer.UriAttributes.Clear();
        sanitizer.UriAttributes.UnionWith(UrlAttributes);

        sanitizer.RemovingTag += (_, e) => removals.Add($"<{e.Tag.NodeName.ToLowerInvariant()}>");
        sanitizer.RemovingStyle += (_, e) => removals.Add($"style:{e.Style.Name}");
        sanitizer.RemovingAtRule += (_, e) => removals.Add("@rule");

        sanitizer.RemovingAttribute += (_, e) =>
        {
            var name = e.Attribute.Name;

            if (IsPermittedExtraAttribute(name) && IsPermittedValue(name, e.Attribute.Value, basePath))
            {
                e.Cancel = true;
                return;
            }

            removals.Add(name.ToLowerInvariant());
        };

        sanitizer.FilterUrl += (_, e) =>
        {
            if (!IsAllowedUrl(e.OriginalUrl, basePath))
            {
                removals.Add($"url:{Shorten(e.OriginalUrl)}");
                e.SanitizedUrl = null;
            }
        };

        return sanitizer;
    }

    /// <summary>
    /// Whether an attribute outside the static allowlist may nonetheless be kept.
    /// </summary>
    /// <remarks>
    /// <c>hx-on</c> in all its spellings is refused here. It is htmx's inline event handler
    /// and is exactly as dangerous as <c>onclick</c>, while looking like ordinary
    /// declarative markup — which is what makes it the interesting one.
    /// </remarks>
    private static bool IsPermittedExtraAttribute(string name)
    {
        if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (name.StartsWith("hx-on", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (AriaAttribute.IsMatch(name))
        {
            return true;
        }

        return IdentifierAttributes.Contains(name) || AllowedHxAttributes.Contains(name);
    }

    private static bool IsPermittedValue(string name, string value, string basePath)
    {
        // `js:` and `javascript:` prefixes make htmx evaluate the value as an expression.
        // hx-vals and hx-headers are not in the allowlist for this reason, but the check
        // stays as a second line in case one is ever added.
        if (value.TrimStart().StartsWith("js:", StringComparison.OrdinalIgnoreCase)
            || value.TrimStart().StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (IdentifierAttributes.Contains(name))
        {
            return IsPermittedIdentifier(value);
        }

        return !UrlAttributes.Contains(name) || IsAllowedUrl(value, basePath);
    }

    /// <summary>
    /// A URL is allowed when it stays inside the plugin's own mount point, is a fragment
    /// reference, or is an absolute https URL — plus small inline images.
    /// </summary>
    internal static bool IsAllowedUrl(string? url, string basePath)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var value = url.Trim();

        // Control characters and whitespace inside a scheme are the classic way to smuggle
        // `javascript:` past a naive prefix check.
        var collapsed = new string([.. value.Where(c => !char.IsControl(c) && !char.IsWhiteSpace(c))]);

        if (collapsed.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
            || collapsed.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase)
            || collapsed.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && !ImageDataUri.IsMatch(value))
        {
            return false;
        }

        if (ImageDataUri.IsMatch(value))
        {
            return true;
        }

        if (value.StartsWith('#'))
        {
            return true;
        }

        if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.TryCreate(value, UriKind.Absolute, out _);
        }

        // Protocol-relative (`//evil.example`) is an absolute URL in disguise.
        if (value.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        if (!value.StartsWith('/'))
        {
            return false;
        }

        var normalized = basePath.TrimEnd('/');

        return value.Equals(normalized, StringComparison.Ordinal)
               || value.StartsWith(normalized + "/", StringComparison.Ordinal)
               || value.StartsWith(normalized + "?", StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether an id or name may be used by a plugin.
    /// </summary>
    /// <remarks>
    /// The rule is refusal rather than prefixing. Rewriting ids on output would mean also
    /// rewriting every <c>hx-target="#..."</c> that refers to them, which is precisely the
    /// attribute rewriting the plugin UI contract rules out. Reserving one namespace gets
    /// the same protection with no rewriting at all.
    /// </remarks>
    internal static bool IsPermittedIdentifier(string? value) =>
        !string.IsNullOrEmpty(value)
        && !value.StartsWith(ReservedIdPrefix, StringComparison.OrdinalIgnoreCase);

    private static string Shorten(string? value) =>
        value is null ? "<null>" : value.Length <= 60 ? value : value[..60] + "…";
}

public sealed record SanitizedFragment(string Html, IReadOnlyList<string> Removed)
{
    public bool WasModified => Removed.Count > 0;
}
