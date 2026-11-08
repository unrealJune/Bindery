using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Bindery.Host.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bindery.Host.Tests;

/// <summary>
/// How the catalog refuses a request that has no business being served.
/// </summary>
/// <remarks>
/// <para>
/// The rest of the suite runs <c>Auth:Mode=None</c>, which answers every request as a
/// signed-in developer and therefore cannot see any of this. These tests stand up a second
/// host in <c>Auth:Mode=Oidc</c> — the only mode a real deployment uses — because the
/// failure being pinned here is a redirect that only exists when the cookie handler does.
/// </para>
/// <para>
/// It matters because ereaders fetch catalogs through LuaSocket, which follows redirects
/// while dropping the credentials on each hop. A 302 to <c>/signin</c> therefore comes back
/// as a 200 full of HTML, and the client reports a parse error instead of asking for a
/// password. This is also the difference between a diagnosable failure and a silent one
/// once the catalog is reachable from outside the tailnet.
/// </para>
/// </remarks>
public sealed class CatalogChallengeTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string _root = string.Empty;

    private HttpClient Client { get; set; } = default!;

    private string Token { get; set; } = string.Empty;

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "bindery-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "library"));
        Directory.CreateDirectory(Path.Combine(_root, "data"));

        var settings = new Dictionary<string, string?>
        {
            ["Bindery:Auth:Mode"] = "Oidc",
            // Never contacted: nothing in these tests challenges the OIDC scheme, which is
            // the point being asserted. It still has to parse, because the CSP's
            // form-action list is derived from it at boot.
            ["Bindery:Auth:Authority"] = "https://auth.invalid/application/o/bindery/",
            ["Bindery:Auth:ClientId"] = "test-client",
            ["Bindery:Auth:ClientSecret"] = "test-secret",
            ["Bindery:DataPath"] = Path.Combine(_root, "data"),
            ["Bindery:LibraryPath"] = Path.Combine(_root, "library"),
            ["Bindery:Plugins:Token"] = "test-token"
        };

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseSetting("ENVIRONMENT", Environments.Development);

            foreach (var (key, value) in settings)
            {
                host.UseSetting(key, value);
            }
        });

        Client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var scope = _factory.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<FeedTokenService>();
        Token = (await tokens.IssueAsync("test device", "tester", CancellationToken.None)).Secret;
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();

        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

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

    [Theory]
    [InlineData("/opds")]
    [InlineData("/opds/new")]
    [InlineData("/opds/v2")]
    [InlineData("/opds/opensearch.xml")]
    public async Task Catalog_without_credentials_asks_for_a_password(string path)
    {
        using var response = await Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // The header ereaders act on. Bearer would be correct and useless: KOReader's
        // catalog screen offers a username and a password box and nothing else.
        var challenge = Assert.Single(response.Headers.WwwAuthenticate);
        Assert.Equal("Basic", challenge.Scheme);

        // A stray Location alongside the 401 would be harmless to LuaSocket, which only
        // follows 3xx — but it would mean the cookie handler still ran its redirect, and
        // the next refactor could easily let the status follow it.
        Assert.False(response.Headers.Contains("Location"));
    }

    [Fact]
    public async Task Catalog_accepts_a_feed_token_as_a_basic_password()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/opds");

        // The username is ignored, so it stands in for whatever a reader puts in that box.
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"kobo:{Token}")));

        using var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Catalog_accepts_a_feed_token_as_a_bearer()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/opds");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        using var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Catalog_answers_a_head_request()
    {
        // opds_plus.koplugin probes with HEAD first to read Last-Modified for its cache,
        // and the public ingress route has to allow the method for that to arrive at all.
        using var request = new HttpRequestMessage(HttpMethod.Head, "/opds");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        using var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_bad_token_is_refused_without_a_redirect()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/opds");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-real-token");

        using var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.Contains("Location"));
    }

    [Fact]
    public async Task The_interface_still_redirects_a_browser_to_sign_in()
    {
        // The guard above is keyed on the request path, so this is the assertion that it
        // did not quietly turn the whole site into a Basic-auth prompt.
        using var response = await Client.GetAsync("/library");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/signin", response.Headers.Location?.AbsolutePath);
    }

    [Fact]
    public async Task A_feed_token_cannot_reach_the_interface()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/library");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        using var response = await Client.SendAsync(request);

        // `UseUi` does not list the feed-token scheme at all, so the credential is not so
        // much rejected as unseen, and the request is treated as anonymous.
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/signin", response.Headers.Location?.AbsolutePath);
    }
}
