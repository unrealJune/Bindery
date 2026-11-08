using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Bindery.Host.Tests;

/// <summary>
/// The reading-position endpoints: push, pull, and the ways a reader can be wrong.
/// </summary>
/// <remarks>
/// Runs against the dev principal ("dev", no token id), which is the only identity dev
/// mode has. Per-token isolation therefore is not reachable here in the round-trip tests;
/// the service upserts by (book, token) and the unique index is asserted by the migration.
/// </remarks>
[Collection(BinderyCollection.Name)]
public sealed class ProgressTests(BinderyFixture fixture)
{
    private const string Document = "urn:bindery:book:{0}";

    private static string Urn(Guid id) => string.Format(Document, id.ToString("N"));

    private static int _work;

    private async Task<Guid> MakeBookAsync()
    {
        // A distinct stub work per call: the stub answers "unchanged" when the URL's book
        // already exists at the same UpdatedAt, and other tests in this collection have
        // certainly downloaded works/7 before we run.
        var url = $"https://stub.invalid/works/{Interlocked.Increment(ref _work) + 100}";

        using var enqueued = await fixture.Client.PostAsJsonAsync("/api/downloads", new { url });

        var accepted = await enqueued.Content.ReadFromJsonAsync<JsonElementJob>();
        Assert.NotNull(accepted);

        var job = await fixture.WaitForJobAsync(accepted.Id);
        Assert.Equal("succeeded", job.Status);
        Assert.NotNull(job.BookId);

        return job.BookId!.Value;
    }

    [Fact]
    public async Task A_push_is_round_trippable_by_the_get_that_follows_it()
    {
        var bookId = await MakeBookAsync();

        using var put = await fixture.Client.PutAsJsonAsync("/opds/syncs/progress", new
        {
            document = Urn(bookId),
            progress = "xpointer(/body/DocFragment[3])",
            percentage = 0.42,
            device = "kindle",
            device_id = "dev-1",
        });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using var get = await fixture.Client.GetAsync($"/opds/syncs/progress/{Urn(bookId)}");

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        var state = await get.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(Urn(bookId), state.GetProperty("document").GetString());
        Assert.Equal("xpointer(/body/DocFragment[3])", state.GetProperty("progress").GetString());
        Assert.Equal(0.42, state.GetProperty("percentage").GetDouble());
        Assert.Equal("kindle", state.GetProperty("device").GetString());
        Assert.Equal("dev-1", state.GetProperty("device_id").GetString());
        Assert.True(state.GetProperty("timestamp").GetInt64() > 0);
    }

    [Fact]
    public async Task A_second_push_overwrites_rather_than_duplicates()
    {
        var bookId = await MakeBookAsync();

        using var first = await fixture.Client.PutAsJsonAsync("/opds/syncs/progress", new
        {
            document = Urn(bookId),
            progress = "page-10",
            percentage = 0.1,
        });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var second = await fixture.Client.PutAsJsonAsync("/opds/syncs/progress", new
        {
            document = Urn(bookId),
            progress = "page-90",
            percentage = 0.9,
            device = "kindle",
        });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        using var get = await fixture.Client.GetAsync($"/opds/syncs/progress/{Urn(bookId)}");
        var state = await get.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("page-90", state.GetProperty("progress").GetString());
        Assert.Equal(0.9, state.GetProperty("percentage").GetDouble());

        using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Bindery.Host.Data.BinderyDbContext>();
        Assert.Equal(1, await db.ReadProgress.CountAsync(p => p.BookId == bookId));
    }

    [Fact]
    public async Task Getting_a_position_for_an_unknown_book_is_404()
    {
        var unknown = Guid.NewGuid().ToString("N");

        using var response = await fixture.Client.GetAsync($"/opds/syncs/progress/urn:bindery:book:{unknown}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Pushing_a_position_for_an_unknown_book_is_404_and_writes_nothing()
    {
        var unknown = Guid.NewGuid().ToString("N");

        using var response = await fixture.Client.PutAsJsonAsync("/opds/syncs/progress", new
        {
            document = Urn(Guid.Parse(unknown)),
            progress = "page-1",
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Bindery.Host.Data.BinderyDbContext>();
        Assert.Equal(0, await db.ReadProgress.CountAsync(p => p.BookId == Guid.Parse(unknown)));
    }

    [Theory]
    [InlineData("urn:bindery:book:not-a-guid")]
    [InlineData("not-a-guid")]
    public async Task A_malformed_document_is_rejected(string document)
    {
        using var get = await fixture.Client.GetAsync($"/opds/syncs/progress/{document}");
        using var put = await fixture.Client.PutAsJsonAsync("/opds/syncs/progress", new
        {
            document,
            progress = "page-1",
        });

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [Fact]
    public async Task A_push_without_a_progress_value_is_a_bad_request()
    {
        var bookId = await MakeBookAsync();

        using var response = await fixture.Client.PutAsJsonAsync("/opds/syncs/progress", new
        {
            document = Urn(bookId),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
