using System.Net;
using System.Net.Http.Json;
using Bindery.Host.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Bindery.Host.Tests;

/// <summary>
/// The plugin surface: discovery, actions, settings, and the fragment proxy.
/// </summary>
[Collection(BinderyCollection.Name)]
public sealed class PluginTests(BinderyFixture fixture)
{
    [Fact]
    public async Task The_configured_plugin_is_discovered_and_healthy()
    {
        var plugins = await fixture.Client.GetFromJsonAsync<List<PluginPayload>>("/api/plugins");

        Assert.NotNull(plugins);

        var stub = Assert.Single(plugins, plugin => plugin.Name == fixture.Plugin.Name);

        Assert.True(stub.Healthy);
        Assert.Null(stub.Error);
        Assert.Equal("fragment", stub.UiMode);
        Assert.Contains("echo", stub.Actions);
    }

    [Fact]
    public async Task An_action_round_trips_through_the_host()
    {
        using var response = await fixture.Client.PostAsJsonAsync(
            $"/api/plugins/{fixture.Plugin.Name}/actions/echo",
            new Dictionary<string, string> { ["text"] = "hello bindery" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("hello bindery", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Secrets_are_stored_encrypted_and_never_read_back()
    {
        using var scope = fixture.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<PluginSettingsStore>();
        var descriptor = scope.ServiceProvider.GetRequiredService<PluginRegistry>().Find(fixture.Plugin.Name);

        Assert.NotNull(descriptor?.Manifest);

        await store.SaveAsync(
            descriptor.Manifest,
            new Dictionary<string, string?> { ["author_name"] = "R. Tester", ["api_key"] = "s3cret-value" },
            CancellationToken.None);

        var display = await store.GetForDisplayAsync(descriptor.Manifest, CancellationToken.None);

        Assert.Equal("R. Tester", display.ValueFor("author_name"));
        Assert.True(display.IsSecretSet("api_key"));
        Assert.DoesNotContain("s3cret-value", display.Values.Values);

        // The plugin still gets the plaintext — that is the whole point of storing it.
        var resolved = await store.GetForPluginAsync(fixture.Plugin.Name, CancellationToken.None);

        Assert.Equal("s3cret-value", resolved["api_key"]);

        await store.ClearAsync(fixture.Plugin.Name, "api_key", CancellationToken.None);
    }

    [Fact]
    public async Task A_browser_navigation_to_the_proxy_lands_on_the_plugin_page()
    {
        using var response = await fixture.Client.GetAsync($"/plugins/{fixture.Plugin.Name}/ui/settings");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(
            $"/plugins/{fixture.Plugin.Name}?path=%2Fsettings",
            response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task A_fragment_is_sanitized_and_wrapped_with_the_antiforgery_token()
    {
        fixture.Plugin.Fragment =
            """
            <div class="bnd-card" onclick="steal()">
              <script>alert(1)</script>
              <a href="javascript:alert(2)">bad</a>
              <button hx-get="/plugins/stub/ui/more">good</button>
            </div>
            """;

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"/plugins/{fixture.Plugin.Name}/ui/");

            request.Headers.Add("HX-Request", "true");

            using var response = await fixture.Client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var html = await response.Content.ReadAsStringAsync();

            Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("onclick", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);

            // htmx attributes survive: they are how a fragment plugin is interactive at all.
            Assert.Contains("hx-get=\"/plugins/stub/ui/more\"", html, StringComparison.Ordinal);

            // CSRF is the host's job, carried by the wrapper for htmx to inherit.
            Assert.Contains("hx-headers=", html, StringComparison.Ordinal);
            Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            fixture.Plugin.Fragment = "<div class=\"bnd-card\">plain</div>";
        }
    }

    [Fact]
    public async Task The_plugin_is_told_where_it_is_mounted()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/plugins/{fixture.Plugin.Name}/ui/inbox");
        request.Headers.Add("HX-Request", "true");

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(fixture.Plugin.Seen, seen => seen.StartsWith("GET /bindery/v1/ui/inbox", StringComparison.Ordinal));
    }

    private sealed record PluginPayload(
        string Name,
        string DisplayName,
        string? Version,
        bool Healthy,
        string? Error,
        DateTimeOffset? RefreshedAt,
        int Patterns,
        string UiMode,
        IReadOnlyList<string> Actions);
}
