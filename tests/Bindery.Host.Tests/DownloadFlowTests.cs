using System.Net;
using System.Net.Http.Json;

namespace Bindery.Host.Tests;

/// <summary>
/// The acquisition path end to end: enqueue, stream, ingest, serve.
/// </summary>
/// <remarks>
/// This is the test that would have caught most of the interesting bugs in this codebase,
/// because it exercises the seam between the plugin transport, the library writer, and the
/// catalog index all at once.
/// </remarks>
[Collection(BinderyCollection.Name)]
public sealed class DownloadFlowTests(BinderyFixture fixture)
{
    [Fact]
    public async Task A_download_becomes_a_book_a_file_and_a_feed_entry()
    {
        using var enqueued = await fixture.Client.PostAsJsonAsync(
            "/api/downloads", new { url = StubPlugin.SupportedUrl });

        Assert.Equal(HttpStatusCode.Created, enqueued.StatusCode);

        var accepted = await enqueued.Content.ReadFromJsonAsync<JsonElementJob>();
        Assert.NotNull(accepted);
        Assert.Equal(fixture.Plugin.Name, accepted.Plugin);

        var job = await fixture.WaitForJobAsync(accepted.Id);

        Assert.Equal("succeeded", job.Status);
        Assert.Null(job.ErrorCode);
        Assert.NotNull(job.BookId);

        using var bookResponse = await fixture.Client.GetAsync($"/api/books/{job.BookId}");
        Assert.Equal(HttpStatusCode.OK, bookResponse.StatusCode);

        var book = await bookResponse.Content.ReadFromJsonAsync<BookPayload>();
        Assert.NotNull(book);
        Assert.Equal("Stub Work 7", book.Title);
        Assert.Equal("Stub Series", book.Series);
        Assert.Contains(book.Files, file => file.Format == "epub" && file.SizeBytes > 0);

        // The file on disk is the source of truth; the database is only an index of it.
        var onDisk = Directory.EnumerateFiles(fixture.LibraryPath, "*.epub", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(onDisk);

        using var download = await fixture.Client.GetAsync($"/opds/download/{job.BookId}.epub");

        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/epub+zip", download.Content.Headers.ContentType?.MediaType);

        var bytes = await download.Content.ReadAsByteArrayAsync();
        Assert.NotEmpty(bytes);

        using var feed = await fixture.Client.GetAsync("/opds/new");
        Assert.Contains("Stub Work 7", await feed.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unroutable_url_is_refused_before_a_job_exists()
    {
        using var response = await fixture.Client.PostAsJsonAsync(
            "/api/downloads", new { url = "https://nobody.invalid/thing" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_failing_plugin_produces_a_failed_job_not_an_exception()
    {
        fixture.Plugin.FailDownloads = true;

        try
        {
            using var enqueued = await fixture.Client.PostAsJsonAsync(
                "/api/downloads", new { url = "https://stub.invalid/works/99" });

            var accepted = await enqueued.Content.ReadFromJsonAsync<JsonElementJob>();
            Assert.NotNull(accepted);

            var job = await fixture.WaitForJobAsync(accepted.Id, TimeSpan.FromSeconds(60));

            Assert.Equal("failed", job.Status);
            Assert.Equal("site_error", job.ErrorCode);
        }
        finally
        {
            fixture.Plugin.FailDownloads = false;
        }
    }

    private sealed record BookPayload(
        Guid Id,
        string Title,
        IReadOnlyList<string> Authors,
        string? Series,
        IReadOnlyList<BookFilePayload> Files);

    private sealed record BookFilePayload(string Format, long SizeBytes);
}
