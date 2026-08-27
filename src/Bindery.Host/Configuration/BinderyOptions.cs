namespace Bindery.Host.Configuration;

/// <summary>
/// Everything Bindery is told at boot. Bound from configuration under "Bindery".
/// </summary>
public sealed class BinderyOptions
{
    public const string SectionName = "Bindery";

    /// <summary>Shown as the catalog title in every OPDS client.</summary>
    public string CatalogTitle { get; set; } = "Bindery";

    public string CatalogSubtitle { get; set; } = "A small self-hosted library";

    /// <summary>
    /// Where books live. This is the source of truth; the database is a rebuildable index.
    /// </summary>
    public string LibraryPath { get; set; } = "/library";

    /// <summary>Where the SQLite file and data-protection keys live.</summary>
    public string DataPath { get; set; } = "/data";

    /// <summary>
    /// Absolute base URL, when Bindery sits behind a proxy that does not forward enough to
    /// reconstruct it. Feed links are relative wherever possible, so this is rarely needed.
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    public int PageSize { get; set; } = 24;

    public AuthOptions Auth { get; set; } = new();

    public DownloadOptions Downloads { get; set; } = new();

    public PluginHostOptions Plugins { get; set; } = new();

    public SecurityOptions Security { get; set; } = new();
}

public sealed class SecurityOptions
{
    /// <summary>
    /// Extra origins appended to the CSP <c>form-action</c> list, on top of <c>'self'</c> and
    /// the OIDC authority (which is added automatically — see
    /// <see cref="SecurityHeaders.BuildContentSecurityPolicy"/>).
    /// </summary>
    /// <remarks>
    /// Only needed when a sign-in or sign-out POST is redirected somewhere other than the
    /// authority: an IdP that posts logout to a separate host, or a proxy that bounces the
    /// callback through a third origin. Browsers enforce <c>form-action</c> across the whole
    /// redirect chain, so an origin missing here is a login that dies silently in the browser
    /// with nothing in the server log.
    /// </remarks>
    public IList<string> FormActionSources { get; set; } = new List<string>();
}

public enum AuthMode
{
    /// <summary>OIDC for the UI, feed tokens for OPDS. The only mode for a real deployment.</summary>
    Oidc,

    /// <summary>
    /// No authentication at all. For local development. Bindery warns loudly at boot and
    /// keeps warning, because "temporarily" is how this ends up facing the internet.
    /// </summary>
    None
}

public sealed class AuthOptions
{
    public AuthMode Mode { get; set; } = AuthMode.Oidc;

    /// <summary>OIDC authority, e.g. https://login.microsoftonline.com/{tenant}/v2.0.</summary>
    public string? Authority { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public IList<string> Scopes { get; set; } = new List<string> { "openid", "profile", "email" };

    /// <summary>
    /// When set, only these subjects or emails may sign in. Empty means anyone the identity
    /// provider lets through, which is usually what a single-tenant IdP already enforces.
    /// </summary>
    public IList<string> AllowedUsers { get; set; } = new List<string>();

    /// <summary>Claim to treat as the display name.</summary>
    public string NameClaim { get; set; } = "name";

    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>How long a signed-in browser session lasts.</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromDays(14);
}

public sealed class DownloadOptions
{
    /// <summary>How many downloads run at once, across all plugins.</summary>
    public int Concurrency { get; set; } = 2;

    /// <summary>Attempts for a failure the plugin marked retryable.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Base delay for retry backoff; doubles each attempt.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a download may go without producing an event before it is abandoned.
    /// Streaming NDJSON exists to keep this honest — see PLAN.md risk 3.
    /// </summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Hard ceiling on a single artifact, enforced by aborting the read.</summary>
    public long MaxArtifactBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>Job rows kept after completion, oldest pruned first.</summary>
    public int HistoryLimit { get; set; } = 500;
}

public sealed class PluginHostOptions
{
    /// <summary>
    /// The shared bearer token sent to every plugin. Comes from a Kubernetes Secret in a
    /// real deployment.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>How often a cached manifest is refreshed.</summary>
    public TimeSpan ManifestRefresh { get; set; } = TimeSpan.FromMinutes(15);

    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan ManifestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan ActionTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Largest fragment the UI proxy will forward, in bytes.</summary>
    public int MaxFragmentBytes { get; set; } = 512 * 1024;

    /// <summary>
    /// Largest sandboxed UI response the proxy will forward, in bytes.
    /// </summary>
    /// <remarks>
    /// Higher than <see cref="MaxFragmentBytes"/> because a sandboxed plugin serves its own
    /// subresources through this path — scripts, stylesheets, fonts, images — not just a
    /// snippet of markup.
    /// </remarks>
    public int MaxSandboxedBytes { get; set; } = 4 * 1024 * 1024;

    public TimeSpan FragmentTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The registry. In Kubernetes this is rendered into a ConfigMap by the Helm chart and
    /// read at boot; there is no filesystem scan and no discovery magic.
    /// </summary>
    public IList<PluginEntry> Registry { get; set; } = new List<PluginEntry>();
}

public sealed class PluginEntry
{
    /// <summary>
    /// Must match the plugin's own manifest name. A mismatch is a configuration error and
    /// is reported as one rather than silently trusting either side.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>Base URL, e.g. http://127.0.0.1:8080 for a sidecar.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Overrides the shared token for this plugin only.</summary>
    public string? Token { get; set; }
}
