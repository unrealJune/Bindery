using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bindery.Host.Tests;

/// <summary>
/// The sandboxed UI tier — docs/PLUGIN-UI.md.
/// </summary>
/// <remarks>
/// These assert the inverse of <see cref="PluginTests"/>'s fragment expectations, and that
/// is the point of the tier rather than a contradiction: a fragment is inlined into
/// Bindery's origin and must therefore be stripped of script, while a sandboxed document is
/// handed to an opaque origin where script has nothing to reach. What replaces the
/// sanitizer is the headers, so the headers are what is tested.
///
/// It runs its own host and stub rather than joining the shared collection fixture, because
/// the tier a plugin declares is fixed at manifest time.
/// </remarks>
public sealed class SandboxedUiTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private StubPlugin _plugin = default!;
    private HttpClient _client = default!;
    private string _root = string.Empty;

    public async Task InitializeAsync()
    {
        _plugin = await StubPlugin.StartAsync(uiMode: "sandboxed");

        _root = Path.Combine(Path.GetTempPath(), "bindery-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "library"));
        Directory.CreateDirectory(Path.Combine(_root, "data"));

        var settings = new Dictionary<string, string?>
        {
            ["Bindery:Auth:Mode"] = "None",
            ["Bindery:DataPath"] = Path.Combine(_root, "data"),
            ["Bindery:LibraryPath"] = Path.Combine(_root, "library"),
            ["Bindery:Downloads:Concurrency"] = "1",
            ["Bindery:Plugins:Token"] = "test-token",
            ["Bindery:Plugins:Registry:0:Name"] = _plugin.Name,
            ["Bindery:Plugins:Registry:0:Enabled"] = "true",
            ["Bindery:Plugins:Registry:0:BaseUrl"] = _plugin.BaseUrl
        };

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseSetting("ENVIRONMENT", Environments.Development);

            foreach (var (key, value) in settings)
            {
                host.UseSetting(key, value);
            }
        });

        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var registry = _factory.Services.GetRequiredService<Plugins.PluginRegistry>();

        for (var attempt = 0; attempt < 100 && registry.Find(_plugin.Name)?.IsUsable != true; attempt++)
        {
            await Task.Delay(100);
        }
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();

        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _plugin.DisposeAsync();

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temp directory that outlives the test run is not worth failing it over.
        }
    }

    [Fact]
    public async Task The_manifest_reports_the_sandboxed_tier()
    {
        var registry = _factory!.Services.GetRequiredService<Plugins.PluginRegistry>();
        var descriptor = registry.Find(_plugin.Name);

        Assert.NotNull(descriptor?.Manifest);
        Assert.Equal("sandboxed", descriptor.Manifest.Ui.Mode.Wire);
        Assert.True(descriptor.Manifest.Ui.Mode.RunsInFrame);
        Assert.False(descriptor.Manifest.Ui.Mode.RequiresSanitizing);
    }

    [Fact]
    public async Task A_sandboxed_document_is_forwarded_verbatim()
    {
        using var response = await _client.GetAsync($"/plugins/{_plugin.Name}/ui/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();

        // Everything the fragment tier strips must survive here. The isolation is what makes
        // that safe, and if this ever starts failing the tier has quietly stopped working.
        Assert.Contains("<!doctype html>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<script src=\"app.js\">", html, StringComparison.Ordinal);
        Assert.Contains("onload=\"boot()\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sandboxed_document_is_not_redirected_to_the_desk_page()
    {
        // The iframe's src is this URL and it is fetched as an ordinary navigation with no
        // HX-Request header. Redirecting it would bounce the frame into the page holding it.
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/plugins/{_plugin.Name}/ui/somewhere");
        request.Headers.Add("Sec-Fetch-Dest", "iframe");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_top_level_navigation_lands_on_the_desk_page()
    {
        // Opened from the address bar, this URL has no parent frame — so the bridge never
        // sends `init` and the plugin sits there loading forever. It belongs on the desk.
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/plugins/{_plugin.Name}/ui/sources");
        request.Headers.Add("Sec-Fetch-Dest", "document");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(
            $"/plugins/{_plugin.Name}?path=%2Fsources",
            response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Navigation_entries_point_at_the_desk_not_the_raw_document()
    {
        using var scope = _factory!.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<Plugins.PluginRegistry>();
        var proxy = scope.ServiceProvider.GetRequiredService<Plugins.PluginUiProxy>();
        var descriptor = registry.Find(_plugin.Name);

        Assert.NotNull(descriptor);

        var nav = proxy.NavigationFor(descriptor);

        Assert.NotEmpty(nav);

        foreach (var entry in nav)
        {
            Assert.StartsWith($"/plugins/{_plugin.Name}?path=", entry.Href, StringComparison.Ordinal);
            Assert.DoesNotContain("/ui/", entry.Href, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_frame_gets_its_own_policy_and_may_be_framed_by_bindery()
    {
        using var response = await _client.GetAsync($"/plugins/{_plugin.Name}/ui/");

        var csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));

        // The load-bearing line: no fetch, no XHR, no WebSocket, no beacon. The bridge is
        // the only way out of the frame rather than merely the recommended one.
        Assert.Contains("connect-src 'none'", csp, StringComparison.Ordinal);

        Assert.Contains("frame-ancestors 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("script-src 'unsafe-inline'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("default-src 'self'", csp, StringComparison.Ordinal);

        // DENY would stop Bindery framing its own plugin; frame-ancestors replaces it.
        Assert.False(response.Headers.Contains("X-Frame-Options"));
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_host_page_keeps_its_strict_policy()
    {
        using var response = await _client.GetAsync("/");

        var csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));

        Assert.Contains("default-src 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("script-src 'self'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", csp, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Subresources_are_forwarded_with_their_own_content_type()
    {
        using var response = await _client.GetAsync($"/plugins/{_plugin.Name}/ui/app.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/javascript", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("postMessage", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_content_type_outside_the_allowlist_is_refused()
    {
        using var response = await _client.GetAsync($"/plugins/{_plugin.Name}/ui/forbidden.bin");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.DoesNotContain("\u0001", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_plugin_desk_frames_the_document_without_allowing_same_origin()
    {
        using var response = await _client.GetAsync($"/plugins/{_plugin.Name}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("<iframe", html, StringComparison.Ordinal);
        Assert.Contains("sandbox=\"allow-scripts allow-forms allow-popups allow-downloads\"", html, StringComparison.Ordinal);

        // The single most important negative assertion in the suite. With
        // allow-same-origin the frame would share Bindery's origin and the entire tier
        // would silently become a same-origin XSS surface.
        Assert.DoesNotContain("allow-same-origin", html, StringComparison.Ordinal);
        Assert.DoesNotContain("allow-popups-to-escape-sandbox", html, StringComparison.Ordinal);
    }
}
