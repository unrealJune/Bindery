using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bindery.Host.Circulation;
using Bindery.Host.Data;
using Bindery.Host.Downloads;
using Bindery.Host.Plugins;
using Bindery.Host.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bindery.Host.Tests;

/// <summary>
/// Several people, one installation: the files are shared and nothing else is.
/// </summary>
/// <remarks>
/// Each assertion here is about what a *second* person can reach, because the first
/// person's view is what every other test in the suite already covers. Someone else's book
/// must be a 404 everywhere — the API, both feeds, the file, the cover — not a 403, which
/// would confirm it exists.
/// </remarks>
[Collection(BinderyCollection.Name)]
public sealed class MultiUserTests(BinderyFixture fixture)
{
    private static int _work;

    private static string NextWork() => $"https://stub.invalid/works/{Interlocked.Increment(ref _work) + 500}";

    private static string Urn(Guid id) => $"urn:bindery:book:{id:N}";

    private async Task<Guid> DownloadAsync(HttpClient client, string url)
    {
        using var enqueued = await client.PostAsJsonAsync("/api/downloads", new { url });
        Assert.Equal(HttpStatusCode.Created, enqueued.StatusCode);

        var accepted = await enqueued.Content.ReadFromJsonAsync<JsonElementJob>();
        var job = await fixture.WaitForJobAsync(accepted!.Id);

        Assert.Equal("succeeded", job.Status);
        Assert.NotNull(job.BookId);
        return job.BookId!.Value;
    }

    private async Task<string> DirectoryOfAsync(Guid bookId)
    {
        using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BinderyDbContext>();
        var directory = await db.Books.Where(book => book.Id == bookId).Select(book => book.DirectoryPath).SingleAsync();
        return Path.Combine(fixture.LibraryPath, directory);
    }

    [Fact]
    public async Task Unified_search_combines_sources_without_crossing_people_or_queuing_anything()
    {
        const string owner = "search-reader";
        const string other = "search-other";
        using var reader = fixture.ClientFor(owner);
        using var someoneElse = fixture.ClientFor(other);
        var mine = await DownloadAsync(reader, NextWork());
        var theirs = await DownloadAsync(someoneElse, NextWork());
        using var scope = fixture.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<CirculationAccountStore>();
        var card = await accounts.ConnectAsync(owner, fixture.Plugin.Name,
            new Dictionary<string, string?> { ["card"] = "29000010000901", ["pin"] = StubPlugin.GoodPin }, CancellationToken.None);
        var otherCard = await accounts.ConnectAsync(other, fixture.Plugin.Name,
            new Dictionary<string, string?> { ["card"] = "29000010000902", ["pin"] = StubPlugin.GoodPin }, CancellationToken.None);
        Assert.Null(card.Problem);
        Assert.Null(otherCard.Problem);

        var settings = scope.ServiceProvider.GetRequiredService<PluginSettingsStore>();
        var manifest = scope.ServiceProvider.GetRequiredService<PluginRegistry>().Find(fixture.Plugin.Name)!.Manifest!;
        await settings.SaveAsync(manifest, owner,
            new Dictionary<string, string?> { ["reader_login"] = "search-readers-login" }, CancellationToken.None);
        var before = fixture.Plugin.Calls.Count;

        using var response = await reader.GetAsync("/search?q=Stub");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains(mine.ToString(), html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(theirs.ToString(), html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(card.Account!.Id.ToString(), html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(otherCard.Account!.Id.ToString(), html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("A Shelf Copy", html, StringComparison.Ordinal);
        Assert.Contains("Borrow in StubDrive", html, StringComparison.Ordinal);
        Assert.Contains("Stub result", html, StringComparison.Ordinal);
        Assert.Contains($"name=\"PreferredPlugin\" value=\"{fixture.Plugin.Name}\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("search-readers-login", html, StringComparison.Ordinal);

        var calls = fixture.Plugin.Calls.Skip(before).ToList();
        var search = Assert.Single(calls, call => call.Request == "POST /bindery/v1/actions/search");
        Assert.Equal("search-readers-login", search.Config?["reader_login"]?.GetValue<string>());
        Assert.Equal(fixture.Services.GetRequiredService<PluginPseudonyms>().For(fixture.Plugin.Name, owner), search.User);
        Assert.Equal("member", search.Role);
        Assert.DoesNotContain(calls, call => call.Request.Contains("/actions/admin_search", StringComparison.Ordinal)
            || call.Request.Contains("/actions/purge", StringComparison.Ordinal)
            || call.Request.Contains("/actions/echo", StringComparison.Ordinal)
            || call.Request == "POST /bindery/v1/download"
            || call.Request == "POST /bindery/v1/circulation/holds");

        var token = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        var cookie = response.Headers.GetValues("Set-Cookie")
            .First(header => header.StartsWith("bindery.antiforgery=", StringComparison.Ordinal)).Split(';')[0];

        using var post = new HttpRequestMessage(HttpMethod.Post, "/search?handler=Hold")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["account"] = card.Account.Id.ToString(),
                ["item"] = "bib-1",
                ["pickup"] = "2",
                ["returnQuery"] = "Stub"
            })
        };
        post.Headers.Add("Cookie", cookie);
        using var held = await reader.SendAsync(post);
        Assert.Equal(HttpStatusCode.Redirect, held.StatusCode);
        Assert.Equal("/holds", held.Headers.Location?.ToString());
        Assert.Single(fixture.Plugin.CardHolds["29000010000901"]);
        Assert.Empty(fixture.Plugin.CardHolds["29000010000902"]);

        using var queue = new HttpRequestMessage(HttpMethod.Post, "/?handler=Queue")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["SourceUrl"] = StubPlugin.SupportedUrl,
                ["PreferredPlugin"] = fixture.Plugin.Name
            })
        };
        queue.Headers.Add("Cookie", cookie);
        using var queued = await reader.SendAsync(queue);
        Assert.Equal(HttpStatusCode.Redirect, queued.StatusCode);
        var target = new Uri(reader.BaseAddress!, queued.Headers.Location!);
        var jobId = Guid.Parse(Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(target.Query)["highlight"].ToString());
        var job = await fixture.WaitForJobAsync(jobId);
        Assert.Equal("succeeded", job.Status);
        using var acquired = await reader.GetAsync($"/api/books/{job.BookId}");
        Assert.Equal(HttpStatusCode.OK, acquired.StatusCode);

        var filtered = await reader.GetStringAsync($"/search?q=Stub&card={otherCard.Account.Id}");
        Assert.DoesNotContain("A Shelf Copy", filtered, StringComparison.Ordinal);
        Assert.Contains("Stub result", filtered, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unified_search_works_without_a_card_and_preserves_shelf_results_when_a_plugin_fails(bool fail)
    {
        using var reader = fixture.ClientFor($"search-without-card-{fail}");
        var book = await DownloadAsync(reader, NextWork());
        fixture.Plugin.FailSearch = fail;

        try
        {
            var html = await reader.GetStringAsync("/search?q=Stub");
            Assert.Contains(book.ToString(), html, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("add a library card", html, StringComparison.Ordinal);
            Assert.Contains(fail ? "Stub search unavailable" : "Stub result", html, StringComparison.Ordinal);
            Assert.DoesNotContain("A Shelf Copy", html, StringComparison.Ordinal);
        }
        finally
        {
            fixture.Plugin.FailSearch = false;
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_unified_search_never_invokes_an_action(string query)
    {
        using var reader = fixture.ClientFor("search-without-query");
        var before = fixture.Plugin.Calls.Count;
        using var response = await reader.GetAsync($"/search?q={Uri.EscapeDataString(query)}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(fixture.Plugin.Calls.Skip(before),
            call => call.Request.StartsWith("POST /bindery/v1/actions/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unified_shelf_paging_keeps_the_query_and_library_filters()
    {
        using var reader = fixture.ClientFor("search-paging");
        for (var index = 0; index < 13; index++)
        {
            await UploadAsync(reader, ShelfTests.BuildEpub($"Search Pager {index:00}", "Pager Author"));
        }

        var first = await reader.GetStringAsync("/search?q=Search%20Pager&medium=digital&page=2");
        var pager = System.Text.RegularExpressions.Regex.Match(first,
            "<nav class=\"pager\" aria-label=\"Shelf search pages\">(.*?)</nav>",
            System.Text.RegularExpressions.RegexOptions.Singleline).Groups[1].Value;
        var next = WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(pager, "href=\"([^\"]+)\"").Groups[1].Value);
        Assert.Contains("shelfPage=1", next, StringComparison.Ordinal);
        Assert.Contains("medium=digital", next, StringComparison.Ordinal);
        Assert.Contains("catalogPage=2", next, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Search Pager 00", first, StringComparison.Ordinal);
        Assert.DoesNotContain("Search Pager 12", first, StringComparison.Ordinal);

        var second = await reader.GetStringAsync(next);
        Assert.Contains("Search Pager 12", second, StringComparison.Ordinal);
        Assert.DoesNotContain("Search Pager 00", second, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_book_on_one_shelf_is_not_found_by_anyone_else()
    {
        using var alice = fixture.ClientFor("alice");
        using var bob = fixture.ClientFor("bob");

        var url = NextWork();
        var id = await DownloadAsync(alice, url);
        var title = $"Stub Work {url[(url.LastIndexOf('/') + 1)..]}";

        using (var mine = await alice.GetAsync($"/api/books/{id}"))
        {
            Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        }

        foreach (var path in new[]
                 {
                     $"/api/books/{id}",
                     $"/opds/book/{id}",
                     $"/opds/v2/book/{id}",
                     $"/opds/download/{id}.epub",
                     $"/opds/cover/{id}",
                     $"/opds/thumb/{id}",
                     $"/opds/syncs/progress/{Urn(id)}"
                 })
        {
            using var response = await bob.GetAsync(path);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{path} answered {response.StatusCode} for someone else's book");
        }

        foreach (var path in new[] { "/api/books", "/opds/all", "/opds/new", $"/opds/search?q={Uri.EscapeDataString(title)}", "/opds/v2/all", "/library" })
        {
            using var response = await bob.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(id.ToString(), body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(id.ToString("N"), body, StringComparison.OrdinalIgnoreCase);

            // A search feed echoes its own query, so only the ID counts there.
            if (!path.Contains("search", StringComparison.Ordinal))
            {
                Assert.DoesNotContain(title, body, StringComparison.Ordinal);
            }
        }

        // Nor can they act on it: no withdrawing it from under its owner, no refreshing it.
        using (var delete = await bob.DeleteAsync($"/api/books/{id}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        }

        using (var refresh = await bob.PostAsync($"/api/books/{id}/refresh", content: null))
        {
            Assert.Equal(HttpStatusCode.BadRequest, refresh.StatusCode);
        }

        using var stillThere = await alice.GetAsync($"/api/books/{id}");
        Assert.Equal(HttpStatusCode.OK, stillThere.StatusCode);
    }

    [Fact]
    public async Task One_work_asked_for_twice_is_filed_once_and_withdrawn_per_person()
    {
        using var alice = fixture.ClientFor("alice");
        using var bob = fixture.ClientFor("bob");

        var url = NextWork();
        var id = await DownloadAsync(alice, url);

        // The stub answers "unchanged" for a work Bindery already holds at the same date, so
        // this is the path where nothing is fetched and bob must still end up holding it.
        Assert.Equal(id, await DownloadAsync(bob, url));

        using (var his = await bob.GetAsync($"/api/books/{id}"))
        {
            Assert.Equal(HttpStatusCode.OK, his.StatusCode);
        }

        var directory = await DirectoryOfAsync(id);
        var sidecar = await File.ReadAllTextAsync(Path.Combine(directory, "bindery.json"));
        Assert.Contains("\"alice\"", sidecar, StringComparison.Ordinal);
        Assert.Contains("\"bob\"", sidecar, StringComparison.Ordinal);

        // Bob lets go: it leaves his shelf, and only his.
        using (var withdrawn = await bob.DeleteAsync($"/api/books/{id}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, withdrawn.StatusCode);
        }

        using (var gone = await bob.GetAsync($"/api/books/{id}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        }

        using (var hers = await alice.GetAsync($"/api/books/{id}"))
        {
            Assert.Equal(HttpStatusCode.OK, hers.StatusCode);
        }

        Assert.True(Directory.Exists(directory), "the files went while someone still held the book");
        Assert.DoesNotContain("\"bob\"", await File.ReadAllTextAsync(Path.Combine(directory, "bindery.json")), StringComparison.Ordinal);

        // The last holder lets go, and only now do the files.
        using (var last = await alice.DeleteAsync($"/api/books/{id}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, last.StatusCode);
        }

        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task A_second_persons_link_to_the_same_work_joins_the_first_persons_book()
    {
        using var alice = fixture.ClientFor("alice");
        using var bob = fixture.ClientFor("bob");

        var url = NextWork();
        var id = await DownloadAsync(alice, url);

        // Not the URL alice pasted, so nothing matches before the plugin answers; only the
        // work it reports says this is the same book.
        Assert.Equal(id, await DownloadAsync(bob, $"{url}/chapters/3"));

        using (var his = await bob.GetAsync($"/api/books/{id}"))
        {
            Assert.Equal(HttpStatusCode.OK, his.StatusCode);
        }

        using (var hers = await alice.GetAsync($"/api/books/{id}"))
        {
            Assert.Equal(HttpStatusCode.OK, hers.StatusCode);
        }

        var sidecar = await File.ReadAllTextAsync(Path.Combine(await DirectoryOfAsync(id), "bindery.json"));
        Assert.Contains("\"alice\"", sidecar, StringComparison.Ordinal);
        Assert.Contains("\"bob\"", sidecar, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_same_bytes_uploaded_by_someone_else_are_shelved_not_refiled()
    {
        using var alice = fixture.ClientFor("alice");
        using var bob = fixture.ClientFor("bob");

        var bytes = ShelfTests.BuildEpub("Passed Hand To Hand", "Shared Reader");

        var first = await UploadAsync(alice, bytes);
        Assert.False(first.AlreadyHeld);

        using (var before = await bob.GetAsync($"/api/books/{first.Id}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, before.StatusCode);
        }

        // "Already held" means on your own shelf, so it is false for bob — and says nothing
        // about whose copy it was.
        var second = await UploadAsync(bob, bytes);
        Assert.Equal(first.Id, second.Id);
        Assert.False(second.AlreadyHeld);

        using (var after = await bob.GetAsync($"/api/books/{first.Id}"))
        {
            Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        }

        var again = await UploadAsync(bob, bytes);
        Assert.True(again.AlreadyHeld);
    }

    [Fact]
    public async Task Reading_positions_are_each_persons_own()
    {
        using var alice = fixture.ClientFor("alice");
        using var bob = fixture.ClientFor("bob");
        using var carol = fixture.ClientFor("carol");

        var url = NextWork();
        var id = await DownloadAsync(alice, url);
        await DownloadAsync(bob, url);

        using (var put = await alice.PutAsJsonAsync("/opds/syncs/progress", new { document = Urn(id), progress = "page-10", percentage = 0.1 }))
        {
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        }

        using (var none = await bob.GetAsync($"/opds/syncs/progress/{Urn(id)}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
        }

        using (var put = await bob.PutAsJsonAsync("/opds/syncs/progress", new { document = Urn(id), progress = "page-90", percentage = 0.9 }))
        {
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        }

        Assert.Equal("page-10", (await alice.GetFromJsonAsync<JsonElement>($"/opds/syncs/progress/{Urn(id)}")).GetProperty("progress").GetString());
        Assert.Equal("page-90", (await bob.GetFromJsonAsync<JsonElement>($"/opds/syncs/progress/{Urn(id)}")).GetProperty("progress").GetString());

        // Someone who does not hold the book cannot write a position against it either.
        using (var stranger = await carol.PutAsJsonAsync("/opds/syncs/progress", new { document = Urn(id), progress = "page-1" }))
        {
            Assert.Equal(HttpStatusCode.NotFound, stranger.StatusCode);
        }
    }

    [Fact]
    public async Task A_job_belongs_to_whoever_queued_it()
    {
        using var alice = fixture.ClientFor("alice");
        using var bob = fixture.ClientFor("bob");

        var url = NextWork();
        using var enqueued = await alice.PostAsJsonAsync("/api/downloads", new { url });
        var job = await enqueued.Content.ReadFromJsonAsync<JsonElementJob>();

        using (var cancel = await bob.DeleteAsync($"/api/downloads/{job!.Id}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, cancel.StatusCode);
        }

        using (var retry = await bob.PostAsync($"/api/downloads/{job.Id}/retry", content: null))
        {
            Assert.Equal(HttpStatusCode.NotFound, retry.StatusCode);
        }

        var finished = await fixture.WaitForJobAsync(job.Id);
        Assert.Equal("succeeded", finished.Status);

        using (var his = await bob.GetAsync($"/api/downloads/{job.Id}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, his.StatusCode);
        }

        Assert.DoesNotContain(job.Id.ToString(), await bob.GetStringAsync("/api/downloads?limit=500"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(job.Id.ToString(), await alice.GetStringAsync("/api/downloads?limit=500"), StringComparison.OrdinalIgnoreCase);

        // An update nobody asked for is visible to whoever holds the book, and nobody else.
        Guid unattended;

        using (var scope = fixture.CreateScope())
        {
            var downloads = scope.ServiceProvider.GetRequiredService<DownloadService>();
            var result = await downloads.EnqueueUpdateAsync(finished.BookId!.Value, null, CancellationToken.None);
            Assert.True(result.Accepted, result.Problem);
            unattended = result.Job!.Id;
        }

        await fixture.WaitForJobAsync(unattended);

        using (var hers = await alice.GetAsync($"/api/downloads/{unattended}"))
        {
            Assert.Equal(HttpStatusCode.OK, hers.StatusCode);
        }

        using (var notHis = await bob.GetAsync($"/api/downloads/{unattended}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, notHis.StatusCode);
        }
    }

    [Fact]
    public async Task Members_are_refused_the_installations_controls()
    {
        using var bob = fixture.ClientFor("bob");
        using var admin = fixture.ClientFor("dev", admin: true);
        var plugin = fixture.Plugin.Name;

        using (var scan = await bob.PostAsync("/api/library/scan", content: null))
        {
            Assert.Equal(HttpStatusCode.Forbidden, scan.StatusCode);
        }

        using (var refresh = await bob.PostAsync($"/api/plugins/{plugin}/refresh", content: null))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refresh.StatusCode);
        }

        // An admin action is refused by the host; the plugin never hears of it.
        var purges = fixture.Plugin.Calls.Count(call => call.Request.EndsWith("/actions/purge", StringComparison.Ordinal));

        using (var purge = await bob.PostAsJsonAsync($"/api/plugins/{plugin}/actions/purge", new Dictionary<string, string>()))
        {
            Assert.Contains("Only an admin", await purge.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        Assert.Equal(purges, fixture.Plugin.Calls.Count(call => call.Request.EndsWith("/actions/purge", StringComparison.Ordinal)));

        using (var purge = await admin.PostAsJsonAsync($"/api/plugins/{plugin}/actions/purge", new Dictionary<string, string>()))
        {
            Assert.Contains("purged", await purge.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // The same for a screen under the plugin's adminPaths.
        var adminScreens = fixture.Plugin.Calls.Count(call => call.Request.StartsWith("GET /bindery/v1/ui/admin", StringComparison.Ordinal));

        using (var screen = await SendUiAsync(bob, $"/plugins/{plugin}/ui/admin"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, screen.StatusCode);
        }

        using (var nested = await SendUiAsync(bob, $"/plugins/{plugin}/ui/admin/../admin/x"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, nested.StatusCode);
        }

        Assert.Equal(adminScreens, fixture.Plugin.Calls.Count(call => call.Request.StartsWith("GET /bindery/v1/ui/admin", StringComparison.Ordinal)));

        using (var screen = await SendUiAsync(admin, $"/plugins/{plugin}/ui/admin"))
        {
            Assert.Equal(HttpStatusCode.OK, screen.StatusCode);
        }

        using (var home = await SendUiAsync(bob, $"/plugins/{plugin}/ui/"))
        {
            Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        }

        // And the menu does not offer what would be refused.
        using var scope = fixture.CreateScope();
        var proxy = scope.ServiceProvider.GetRequiredService<PluginUiProxy>();
        var descriptor = scope.ServiceProvider.GetRequiredService<PluginRegistry>().Find(plugin)!;

        Assert.DoesNotContain(proxy.NavigationFor(descriptor, isAdmin: false), entry => entry.Label == "Admin");
        Assert.Contains(proxy.NavigationFor(descriptor, isAdmin: true), entry => entry.Label == "Admin");
    }

    [Fact]
    public async Task A_plugin_is_told_who_is_acting_by_pseudonym_and_cannot_be_lied_to()
    {
        using var alice = fixture.ClientFor("alice");
        using var bob = fixture.ClientFor("bob");
        using var admin = fixture.ClientFor("dev", admin: true);

        var pseudonyms = fixture.Services.GetRequiredService<PluginPseudonyms>();
        var plugin = fixture.Plugin.Name;
        var alicePseudonym = pseudonyms.For(plugin, "alice");
        var bobPseudonym = pseudonyms.For(plugin, "bob");

        Assert.NotEqual("alice", alicePseudonym);
        Assert.NotEqual(alicePseudonym, bobPseudonym);
        Assert.NotEqual(alicePseudonym, pseudonyms.For("another-plugin", "alice"));

        var echo = await EchoAsync(alice);
        Assert.Equal(alicePseudonym, echo.User);
        Assert.Equal(BinderyClaims.MemberRole, echo.Role);

        echo = await EchoAsync(admin);
        Assert.Equal(pseudonyms.For(plugin, "dev"), echo.User);
        Assert.Equal(BinderyClaims.AdminRole, echo.Role);

        // Headers a browser sends are not forwarded: bob cannot claim to be alice, or an admin.
        var before = fixture.Plugin.Calls.Count;
        using (var request = new HttpRequestMessage(HttpMethod.Get, $"/plugins/{plugin}/ui/whoami"))
        {
            request.Headers.Add("HX-Request", "true");
            request.Headers.Add("X-Bindery-User", alicePseudonym);
            request.Headers.Add("X-Bindery-Role", BinderyClaims.AdminRole);

            using var response = await bob.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var ui = fixture.Plugin.Calls.Skip(before).Single(call => call.Request.StartsWith("GET /bindery/v1/ui/whoami", StringComparison.Ordinal));
        Assert.Equal(bobPseudonym, ui.User);
        Assert.Equal(BinderyClaims.MemberRole, ui.Role);

        // A download acts for its requester — always as a member, whoever that is.
        before = fixture.Plugin.Calls.Count;
        await DownloadAsync(admin, NextWork());

        var download = fixture.Plugin.Calls.Skip(before).First(call => call.Request == "POST /bindery/v1/download");
        Assert.Equal(pseudonyms.For(plugin, "dev"), download.User);
        Assert.Equal(BinderyClaims.MemberRole, download.Role);
    }

    [Fact]
    public async Task A_personal_setting_reaches_only_its_owners_calls()
    {
        using var alice = fixture.ClientFor("alice");
        using var bob = fixture.ClientFor("bob");

        using (var scope = fixture.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<PluginSettingsStore>();
            var manifest = scope.ServiceProvider.GetRequiredService<PluginRegistry>().Find(fixture.Plugin.Name)!.Manifest!;

            await store.SaveAsync(manifest, "alice", new Dictionary<string, string?> { ["reader_login"] = "alice-login" }, CancellationToken.None);

            // A personal key submitted to the install section is not an install value.
            await store.SaveAsync(manifest, null, new Dictionary<string, string?> { ["reader_login"] = "everyones-login" }, CancellationToken.None);

            Assert.Equal("alice-login", (await store.GetForPluginAsync(manifest, "alice", CancellationToken.None))["reader_login"]);
            Assert.False((await store.GetForPluginAsync(manifest, "bob", CancellationToken.None)).ContainsKey("reader_login"));
            Assert.False((await store.GetForPluginAsync(manifest, null, CancellationToken.None)).ContainsKey("reader_login"));
        }

        try
        {
            Assert.Equal("alice-login", (await EchoAsync(alice)).Config?["reader_login"]?.GetValue<string>());
            Assert.Null((await EchoAsync(bob)).Config?["reader_login"]);
        }
        finally
        {
            await DeleteSettingAsync("reader_login");
        }
    }

    [Fact]
    public async Task A_shared_value_for_a_key_that_became_personal_goes_to_the_founder()
    {
        using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BinderyDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<PluginSettingsStore>();
        var manifest = scope.ServiceProvider.GetRequiredService<PluginRegistry>().Find(fixture.Plugin.Name)!.Manifest!;

        Assert.Equal("dev", await scope.ServiceProvider.GetRequiredService<UserDirectory>().FounderAsync(CancellationToken.None));

        await DeleteSettingAsync("reader_login");

        // What an install from before scopes holds: one login, everybody's.
        db.PluginSettings.Add(new PluginSettingEntity
        {
            Plugin = fixture.Plugin.Name,
            Owner = null,
            Key = "reader_login",
            Value = "the-founders-login"
        });
        await db.SaveChangesAsync();

        try
        {
            Assert.False((await store.GetForPluginAsync(manifest, "alice", CancellationToken.None)).ContainsKey("reader_login"));
            Assert.Equal("the-founders-login", (await store.GetForPluginAsync(manifest, "dev", CancellationToken.None))["reader_login"]);

            var rows = await db.PluginSettings.AsNoTracking()
                .Where(setting => setting.Plugin == fixture.Plugin.Name && setting.Key == "reader_login")
                .ToListAsync();

            Assert.Equal("dev", Assert.Single(rows).Owner);
        }
        finally
        {
            await DeleteSettingAsync("reader_login");
        }
    }

    [Fact]
    public async Task The_settings_page_shows_a_member_only_their_own_section()
    {
        using var bob = fixture.ClientFor("bob");
        using var admin = fixture.ClientFor("dev", admin: true);

        var page = $"/plugins/{fixture.Plugin.Name}/settings";

        var member = await bob.GetStringAsync(page);
        Assert.Contains("Your login", member, StringComparison.Ordinal);
        Assert.DoesNotContain("Pretend API key", member, StringComparison.Ordinal);
        Assert.DoesNotContain("Author to report", member, StringComparison.Ordinal);

        var both = await admin.GetStringAsync(page);
        Assert.Contains("Your login", both, StringComparison.Ordinal);
        Assert.Contains("Author to report", both, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Saving_your_settings_on_a_plugins_settings_page_keeps_them_yours()
    {
        using var carol = fixture.ClientFor("carol");
        var page = $"/plugins/{fixture.Plugin.Name}/settings";

        using var form = await carol.GetAsync(page);
        var html = await form.Content.ReadAsStringAsync();
        var token = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        var cookie = form.Headers.GetValues("Set-Cookie")
            .First(header => header.StartsWith("bindery.antiforgery=", StringComparison.Ordinal))
            .Split(';')[0];

        using var post = new HttpRequestMessage(HttpMethod.Post, page + "?handler=Save")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["scope"] = Pages.PluginSettingsModel.PersonalScope,
                ["field:reader_login"] = "carols-login"
            })
        };
        post.Headers.Add("Cookie", cookie);

        try
        {
            using var saved = await carol.SendAsync(post);

            Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
            Assert.Equal(page, saved.Headers.Location?.ToString());

            using var scope = fixture.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<PluginSettingsStore>();
            var manifest = scope.ServiceProvider.GetRequiredService<PluginRegistry>().Find(fixture.Plugin.Name)!.Manifest!;

            Assert.Equal("carols-login", (await store.GetForPluginAsync(manifest, "carol", CancellationToken.None))["reader_login"]);
        }
        finally
        {
            await DeleteSettingAsync("reader_login");
        }
    }

    [Fact]
    public async Task A_rescan_restores_shelves_from_the_sidecars()
    {
        using var alice = fixture.ClientFor("alice");
        using var bob = fixture.ClientFor("bob");
        using var admin = fixture.ClientFor("dev", admin: true);

        var id = await DownloadAsync(alice, NextWork());

        // The database is an index; losing its shelf rows must not lose anyone's shelf.
        using (var scope = fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BinderyDbContext>();
            Assert.Equal(1, await db.Shelves.Where(entry => entry.BookId == id).ExecuteDeleteAsync());
        }

        using (var lost = await alice.GetAsync($"/api/books/{id}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, lost.StatusCode);
        }

        using (var scan = await admin.PostAsync("/api/library/scan", content: null))
        {
            Assert.Equal(HttpStatusCode.OK, scan.StatusCode);
        }

        using (var restored = await alice.GetAsync($"/api/books/{id}"))
        {
            Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        }

        using (var still = await bob.GetAsync($"/api/books/{id}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, still.StatusCode);
        }
    }

    [Fact]
    public async Task A_feed_token_is_listed_and_revoked_only_by_its_owner()
    {
        using var scope = fixture.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<FeedTokenService>();

        var issued = await tokens.IssueAsync("alice's kobo", "alice", CancellationToken.None);
        var id = issued.Entity.Id;
        Assert.Contains(await tokens.ListAsync("alice", CancellationToken.None), token => token.Id == id);

        Assert.DoesNotContain(await tokens.ListAsync("bob", CancellationToken.None), token => token.Id == id);
        Assert.False(await tokens.RevokeAsync(id, "bob", CancellationToken.None));

        // The token still works after bob's attempt, and reads as alice's.
        Assert.Equal("alice", (await tokens.ValidateAsync(issued.Secret, CancellationToken.None))?.Subject);

        Assert.True(await tokens.RevokeAsync(id, "alice", CancellationToken.None));
    }

    // ------------------------------------------------------------ helpers

    private async Task<StubPlugin.Call> EchoAsync(HttpClient client)
    {
        var before = fixture.Plugin.Calls.Count;

        using var response = await client.PostAsJsonAsync(
            $"/api/plugins/{fixture.Plugin.Name}/actions/echo",
            new Dictionary<string, string> { ["text"] = "who am I" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return fixture.Plugin.Calls.Skip(before).Single(call => call.Request == "POST /bindery/v1/actions/echo");
    }

    private static Task<HttpResponseMessage> SendUiAsync(HttpClient client, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("HX-Request", "true");
        return client.SendAsync(request);
    }

    private static async Task<UploadPayload> UploadAsync(HttpClient client, byte[] bytes)
    {
        using var form = new MultipartFormDataContent { { new ByteArrayContent(bytes), "file", "shared.epub" } };
        using var response = await client.PostAsync("/api/library/upload", form);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<UploadPayload>())!;
    }

    private async Task DeleteSettingAsync(string key)
    {
        using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BinderyDbContext>();
        await db.PluginSettings.Where(setting => setting.Plugin == fixture.Plugin.Name && setting.Key == key).ExecuteDeleteAsync();
    }

    private sealed record UploadPayload(Guid Id, string Title, bool AlreadyHeld);
}
