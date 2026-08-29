using Bindery.Host.Catalog;
using Bindery.Host.Configuration;
using Bindery.Host.Data;
using Bindery.Host.Downloads;
using Bindery.Host.Endpoints;
using Bindery.Host.Library;
using Bindery.Host.Opds;
using Bindery.Host.Plugins;
using Bindery.Host.Security;
using Bindery.Host.Ui;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOptions<BinderyOptions>()
    .Bind(builder.Configuration.GetSection(BinderyOptions.SectionName))
    .ValidateOnStart();

var options = builder.Configuration.GetSection(BinderyOptions.SectionName).Get<BinderyOptions>() ?? new BinderyOptions();

Directory.CreateDirectory(options.DataPath);
Directory.CreateDirectory(options.LibraryPath);

// ---------------------------------------------------------------- storage

builder.Services.AddDbContext<BinderyDbContext>(db =>
    db.UseSqlite($"Data Source={Path.Combine(options.DataPath, "bindery.db")}"));

// Keys on the data volume, not in memory: losing them means every stored plugin secret
// becomes unrecoverable and every session is invalidated on restart.
builder.Services
    .AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(options.DataPath, "keys")))
    .SetApplicationName("Bindery");

// ---------------------------------------------------------------- plugins

builder.Services.AddHttpClient(PluginClient.RequestClient, http =>
{
    http.Timeout = TimeSpan.FromSeconds(60);
    http.DefaultRequestHeaders.UserAgent.ParseAdd($"Bindery/{ThisAssembly.Version}");
});

// A separate client with no overall timeout: a download legitimately runs for minutes, and
// its liveness is enforced by the per-event idle timeout instead.
builder.Services.AddHttpClient(PluginClient.StreamClient, http =>
{
    http.Timeout = Timeout.InfiniteTimeSpan;
    http.DefaultRequestHeaders.UserAgent.ParseAdd($"Bindery/{ThisAssembly.Version}");
});

builder.Services.AddSingleton<PluginNotifyTokens>();
builder.Services.AddSingleton<PluginClient>();
builder.Services.AddSingleton<PluginRegistry>();
builder.Services.AddSingleton<PluginResolver>();
builder.Services.AddHostedService<PluginRefreshService>();

builder.Services.AddScoped<PluginSettingsStore>();
builder.Services.AddScoped<PluginActionService>();

// ---------------------------------------------------------------- library and downloads

builder.Services.AddSingleton<LibraryStore>();
builder.Services.AddSingleton<DownloadQueue>();
builder.Services.AddScoped<CatalogService>();
builder.Services.AddScoped<DownloadService>();
builder.Services.AddScoped<DownloadRunner>();
builder.Services.AddScoped<BookIndexer>();
builder.Services.AddScoped<LibraryScanner>();
builder.Services.AddScoped<LibraryWriter>();
builder.Services.AddHostedService<DownloadWorker>();
builder.Services.AddHostedService<UpdateScheduler>();

// ---------------------------------------------------------------- web

builder.Services.AddBinderyAuth(options);
builder.Services.AddScoped<FeedTokenService>();
builder.Services.AddSingleton<FragmentSanitizer>();
builder.Services.AddScoped<PluginUiProxy>();
builder.Services.AddSingleton<SourceLabels>();

builder.Services.AddRazorPages(razor =>
{
    razor.Conventions.AuthorizeFolder("/", AuthPolicies.UseUi);
    razor.Conventions.AllowAnonymousToPage("/Signin");
    razor.Conventions.AllowAnonymousToPage("/Denied");
    razor.Conventions.AllowAnonymousToPage("/Error");
});

builder.Services.AddAntiforgery(antiforgery =>
{
    // Named explicitly because plugin fragments are wrapped in a container that carries
    // this header for them — see docs/PLUGIN-UI.md, rule 4.
    antiforgery.HeaderName = "X-CSRF-TOKEN";
    antiforgery.Cookie.Name = "bindery.antiforgery";
    antiforgery.Cookie.SameSite = SameSiteMode.Strict;
});

builder.Services.AddResponseCompression();

// An EPUB is bigger than Kestrel's 30 MB default allows once it carries art, and a limit
// the server enforces below the configured one would only fail uploads confusingly.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = options.Uploads.MaxBytes);
builder.Services.Configure<FormOptions>(form =>
{
    form.MultipartBodyLengthLimit = options.Uploads.MaxBytes;
    form.MultipartHeadersLengthLimit = 32 * 1024;
});

builder.Services.Configure<ForwardedHeadersOptions>(forwarded =>
{
    forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    // Bindery sits behind an ingress it does not know the address of. Restricting this is
    // the deployment's job, via the chart's trustedProxies value.
    forwarded.KnownNetworks.Clear();
    forwarded.KnownProxies.Clear();
});

var app = builder.Build();

// ---------------------------------------------------------------- pipeline

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
}

app.UseResponseCompression();
app.UseStaticFiles();
app.UseSecurityHeaders(options);
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();
app.MapOpds();
app.MapApi();
app.MapPluginNotify();
app.MapPluginUi();

app.MapGet("/healthz", () => Results.Json(new { status = "ok", version = ThisAssembly.Version }))
   .AllowAnonymous();

app.MapGet("/readyz", async (BinderyDbContext db, CancellationToken ct) =>
        await db.Database.CanConnectAsync(ct) ? Results.Ok() : Results.StatusCode(503))
   .AllowAnonymous();

// ---------------------------------------------------------------- start

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BinderyDbContext>();
    await db.Database.MigrateAsync();

    scope.ServiceProvider.GetRequiredService<LibraryStore>().EnsureRoot();
}

if (options.Auth.Mode == AuthMode.None)
{
    app.Logger.LogWarning(
        "Auth:Mode is None. Every request is treated as an authenticated local developer, " +
        "the catalog is open to anyone who can reach this port, and stored plugin secrets " +
        "are readable through the UI. Never run this outside your own machine.");
}

app.Logger.LogInformation(
    "Bindery {Version} serving library {Library} with {Plugins} configured plugin(s)",
    ThisAssembly.Version,
    options.LibraryPath,
    options.Plugins.Registry.Count(entry => entry.Enabled));

await app.RunAsync();

/// <summary>Exposed so the integration tests can build a host from this exact pipeline.</summary>
public partial class Program;

internal static class ThisAssembly
{
    public static string Version { get; } =
        typeof(ThisAssembly).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
