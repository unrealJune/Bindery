using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bindery.Host.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bindery.Host.Tests;

/// <summary>A host with two downloaders, "stub" and "mirror", that can fetch the same work.</summary>
public sealed class MultiSourceFixture : BinderyFixture
{
    protected override IReadOnlyList<string> ExtraPluginNames => ["mirror"];

    public StubPlugin Mirror => Extra["mirror"];
}

[CollectionDefinition(Name)]
public sealed class MultiSourceCollection : ICollectionFixture<MultiSourceFixture>
{
    public const string Name = "bindery-multi-source";
}

/// <summary>
/// One book, several sources: fetching into an existing book through another plugin,
/// switching which source is followed, recognising a known source on intake, and merging
/// two books that turn out to be the same work.
/// </summary>
[Collection(MultiSourceCollection.Name)]
public sealed class SourceTests(MultiSourceFixture fixture)
{
    private static string StubUrl(int work) => $"https://stub.invalid/works/{work}";

    private static string MirrorUrl(int work) => $"https://mirror.invalid/works/{work}";

    [Fact]
    public async Task A_url_fetched_into_a_book_becomes_a_source_without_taking_over_updates()
    {
        var bookId = await DownloadAsync(StubUrl(21));

        var job = await AddSourceAsync(bookId, MirrorUrl(21), follow: false);
        Assert.Equal("succeeded", job.Status);
        Assert.Equal("mirror", job.Plugin);
        Assert.Equal(bookId, job.BookId);

        var sources = await SourcesAsync(bookId);
        Assert.Equal(2, sources.Count);
        Assert.True(sources.Single(s => s.Plugin == "stub").Followed, "following stays where it was");
        Assert.True(sources.Single(s => s.Plugin == "mirror").CurrentFiles, "but the files came from the mirror");
        Assert.Equal("stub", (await BookAsync(bookId)).SourcePlugin);

        // One book on disk, not two, and a rescan from the sidecars rebuilds both sources.
        Assert.Single(EpubsFor("Stub Work 21"));
        using (var scan = await fixture.Client.PostAsync("/api/library/scan", null))
        {
            Assert.Equal(HttpStatusCode.OK, scan.StatusCode);
        }

        Assert.Equal(2, (await SourcesAsync(bookId)).Count);
    }

    [Fact]
    public async Task Following_another_source_moves_updates_to_it()
    {
        var bookId = await DownloadAsync(StubUrl(22));
        await AddSourceAsync(bookId, MirrorUrl(22), follow: false);
        var mirror = (await SourcesAsync(bookId)).Single(s => s.Plugin == "mirror");

        using var follow = await fixture.Client.PostAsync($"/api/books/{bookId}/sources/{mirror.Id}/follow", null);
        Assert.Equal(HttpStatusCode.Accepted, follow.StatusCode);
        var fetched = await fixture.WaitForJobAsync((await follow.Content.ReadFromJsonAsync<JsonElementJob>())!.Id);
        Assert.Contains(fetched.Status, new[] { "succeeded", "unchanged" });

        Assert.Equal("mirror", (await BookAsync(bookId)).SourcePlugin);

        using var refresh = await fixture.Client.PostAsync($"/api/books/{bookId}/refresh", null);
        var update = (await refresh.Content.ReadFromJsonAsync<JsonElementJob>())!;
        Assert.Equal("mirror", update.Plugin);
        await fixture.WaitForJobAsync(update.Id);
    }

    [Fact]
    public async Task Fetching_a_source_whose_copy_is_not_current_asks_for_a_full_copy()
    {
        var bookId = await DownloadAsync(StubUrl(25));
        await AddSourceAsync(bookId, MirrorUrl(25), follow: false); // files now come from the mirror
        var stub = (await SourcesAsync(bookId)).Single(s => s.Plugin == "stub");

        using var fetch = await fixture.Client.PostAsync($"/api/books/{bookId}/sources/{stub.Id}/fetch", null);
        var job = await fixture.WaitForJobAsync((await fetch.Content.ReadFromJsonAsync<JsonElementJob>())!.Id);

        // Had the host passed knownUpdated, the stub would have answered "unchanged" and the
        // mirror's files would have stayed, whatever the person asked for.
        Assert.Equal("succeeded", job.Status);
        Assert.Null(fixture.Plugin.LastDownloadOptions?["knownUpdated"]);
        Assert.True((await SourcesAsync(bookId)).Single(s => s.Plugin == "stub").CurrentFiles);
    }

    [Fact]
    public async Task A_download_from_any_known_source_lands_on_the_same_book()
    {
        var bookId = await DownloadAsync(StubUrl(23));
        await AddSourceAsync(bookId, MirrorUrl(23), follow: false);

        // The mirror's copy is current, so the answer may well be "nothing new" — the point
        // is which book it was asked about.
        using var enqueued = await fixture.Client.PostAsJsonAsync("/api/downloads", new { url = MirrorUrl(23) });
        var again = await fixture.WaitForJobAsync((await enqueued.Content.ReadFromJsonAsync<JsonElementJob>())!.Id);

        Assert.Contains(again.Status, new[] { "succeeded", "unchanged" });
        Assert.Equal(bookId, again.BookId);
        Assert.Single(EpubsFor("Stub Work 23"));
    }

    [Fact]
    public async Task Two_books_of_one_work_are_suggested_and_merge_into_one()
    {
        var keptId = await DownloadAsync(StubUrl(24));
        var otherJob = await DownloadJobAsync(MirrorUrl(24));
        var otherId = otherJob.BookId!.Value;
        Assert.NotEqual(keptId, otherId);

        var detail = await fixture.Client.GetFromJsonAsync<JsonElement>($"/api/downloads/{otherJob.Id}");
        Assert.Contains(detail.GetProperty("log").EnumerateArray(),
            line => line.GetProperty("message").GetString()!.Contains("looks like", StringComparison.Ordinal));

        var duplicates = await fixture.Client.GetFromJsonAsync<List<DuplicatePayload>>($"/api/books/{keptId}/duplicates");
        var suggested = Assert.Single(duplicates!, d => d.BookId == otherId);
        Assert.False(suggested.Certain, "a title match is a suggestion, never automatic");
        Assert.Equal(0.95, suggested.Score);

        using var merge = await fixture.Client.PostAsJsonAsync(
            $"/api/books/{keptId}/merge", new { other = otherId, keep = "other", follow = true });
        Assert.Equal(HttpStatusCode.OK, merge.StatusCode);

        using var gone = await fixture.Client.GetAsync($"/api/books/{otherId}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);

        var book = await BookAsync(keptId);
        Assert.Equal("mirror", book.SourcePlugin);
        Assert.Equal(2, (await SourcesAsync(keptId)).Count);
        Assert.Single(EpubsFor("Stub Work 24"));

        // The merged book's history includes the other book's download.
        using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BinderyDbContext>();
        Assert.True(await db.Jobs.AnyAsync(j => j.Id == otherJob.Id && j.BookId == keptId));
    }

    [Fact]
    public async Task The_book_page_shows_sources_and_suggested_duplicates()
    {
        var bookId = await DownloadAsync(StubUrl(27));
        await DownloadAsync(MirrorUrl(27));

        using var response = await fixture.Client.GetAsync($"/Book/{bookId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Followed for updates", html, StringComparison.Ordinal);
        Assert.Contains("Fetch this book from another URL", html, StringComparison.Ordinal);
        Assert.Contains("same title and author", html, StringComparison.Ordinal);
        Assert.Contains("Merge into this book", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_followed_source_cannot_be_forgotten()
    {
        var bookId = await DownloadAsync(StubUrl(26));
        var followed = Assert.Single(await SourcesAsync(bookId));

        using var response = await fixture.Client.DeleteAsync($"/api/books/{bookId}/sources/{followed.Id}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Sources_and_duplicates_of_a_book_not_on_your_shelf_are_not_found()
    {
        var bookId = await DownloadAsync(StubUrl(28));

        foreach (var request in new[]
                 {
                     As("stranger", HttpMethod.Get, $"/api/books/{bookId}/sources"),
                     As("stranger", HttpMethod.Get, $"/api/books/{bookId}/duplicates"),
                     As("stranger", HttpMethod.Post, $"/api/books/{bookId}/sources",
                         new { url = MirrorUrl(28), plugin = (string?)null, follow = true })
                 })
        {
            using var response = await fixture.Client.SendAsync(request);
            Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest,
                $"{request.Method} {request.RequestUri} answered {response.StatusCode}");
        }

        Assert.Single(await SourcesAsync(bookId)); // the stranger's fetch never ran
    }

    [Fact]
    public async Task Merging_a_book_someone_else_also_holds_leaves_their_copy_alone()
    {
        var keptId = await DownloadAsync(StubUrl(29));
        var otherId = await DownloadAsync(MirrorUrl(29));

        // A second person shelves the mirror copy too.
        using (var shelve = await fixture.Client.SendAsync(As("friend", HttpMethod.Post, "/api/downloads", new { url = MirrorUrl(29) })))
        {
            var job = await fixture.WaitForJobAsync((await shelve.Content.ReadFromJsonAsync<JsonElementJob>())!.Id);
            Assert.Equal(otherId, job.BookId);
        }

        using var merge = await fixture.Client.PostAsJsonAsync(
            $"/api/books/{keptId}/merge", new { other = otherId, keep = "target", follow = false });
        Assert.Equal(HttpStatusCode.OK, merge.StatusCode);

        // Gone from the merger's shelf, with its source now on the kept book...
        using (var mine = await fixture.Client.GetAsync($"/api/books/{otherId}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, mine.StatusCode);
        }

        Assert.Contains(await SourcesAsync(keptId), source => source.Plugin == "mirror");

        // ...and still on the friend's, files and all.
        using var theirs = await fixture.Client.SendAsync(As("friend", HttpMethod.Get, $"/api/books/{otherId}"));
        Assert.Equal(HttpStatusCode.OK, theirs.StatusCode);
        Assert.Equal(2, EpubsFor("Stub Work 29").Count);
    }

    // ------------------------------------------------------------ helpers

    private static HttpRequestMessage As(string subject, HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(TestUserHandler.UserHeader, subject);
        request.Headers.Add(TestUserHandler.RoleHeader, "user");

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    private async Task<JsonElementJob> DownloadJobAsync(string url)
    {
        using var enqueued = await fixture.Client.PostAsJsonAsync("/api/downloads", new { url });
        Assert.Equal(HttpStatusCode.Created, enqueued.StatusCode);
        var job = await fixture.WaitForJobAsync((await enqueued.Content.ReadFromJsonAsync<JsonElementJob>())!.Id);
        Assert.Equal("succeeded", job.Status);
        return job;
    }

    private async Task<Guid> DownloadAsync(string url) => (await DownloadJobAsync(url)).BookId!.Value;

    private async Task<JsonElementJob> AddSourceAsync(Guid bookId, string url, bool follow)
    {
        using var response = await fixture.Client.PostAsJsonAsync(
            $"/api/books/{bookId}/sources", new { url, plugin = (string?)null, follow });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return await fixture.WaitForJobAsync((await response.Content.ReadFromJsonAsync<JsonElementJob>())!.Id);
    }

    private async Task<List<SourcePayload>> SourcesAsync(Guid bookId) =>
        (await fixture.Client.GetFromJsonAsync<List<SourcePayload>>($"/api/books/{bookId}/sources"))!;

    private async Task<BookPayload> BookAsync(Guid bookId) =>
        (await fixture.Client.GetFromJsonAsync<BookPayload>($"/api/books/{bookId}"))!;

    private List<string> EpubsFor(string title) =>
        [.. Directory.EnumerateFiles(fixture.LibraryPath, "*.epub", SearchOption.AllDirectories)
            .Where(path => path.Contains(title, StringComparison.Ordinal))];

    private sealed record SourcePayload(int Id, string Plugin, string Url, bool Followed, bool CurrentFiles);

    private sealed record DuplicatePayload(Guid BookId, string Title, double Score, bool Certain);

    private sealed record BookPayload(Guid Id, string Title, string? SourcePlugin, string? SourceUrl);
}
