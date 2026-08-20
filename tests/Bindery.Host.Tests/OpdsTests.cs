using System.Net;

namespace Bindery.Host.Tests;

/// <summary>
/// The feed's shape, with particular attention to content types.
/// </summary>
/// <remarks>
/// Ereaders route on <c>kind=navigation</c> versus <c>kind=acquisition</c> and genuinely
/// break when it is wrong, which is why these assertions are on the header rather than the
/// body.
/// </remarks>
[Collection(BinderyCollection.Name)]
public sealed class OpdsTests(BinderyFixture fixture)
{
    [Fact]
    public async Task Root_feed_is_navigation()
    {
        using var response = await fixture.Client.GetAsync("/opds");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertContentType("application/atom+xml;profile=opds-catalog;kind=navigation", response);

        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("<feed", body, StringComparison.Ordinal);
        Assert.Contains("/opds/new", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/opds/new")]
    [InlineData("/opds/all")]
    [InlineData("/opds/search?q=stub")]
    public async Task Listing_feeds_are_acquisition(string path)
    {
        using var response = await fixture.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertContentType("application/atom+xml;profile=opds-catalog;kind=acquisition", response);
    }

    [Theory]
    [InlineData("/opds/authors")]
    [InlineData("/opds/series")]
    [InlineData("/opds/tags")]
    public async Task Browse_feeds_are_navigation(string path)
    {
        using var response = await fixture.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertContentType("application/atom+xml;profile=opds-catalog;kind=navigation", response);
    }

    [Fact]
    public async Task Opensearch_document_is_served_with_its_own_type()
    {
        using var response = await fixture.Client.GetAsync("/opds/opensearch.xml");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "application/opensearchdescription+xml",
            response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("/opds/search?q={searchTerms}", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Opds2_feed_is_json()
    {
        using var response = await fixture.Client.GetAsync("/opds/v2/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/opds+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Health_endpoints_answer()
    {
        using var health = await fixture.Client.GetAsync("/healthz");
        using var ready = await fixture.Client.GetAsync("/readyz");

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    /// <summary>Compares content types ignoring the whitespace .NET inserts between parameters.</summary>
    private static void AssertContentType(string expected, HttpResponseMessage response)
    {
        var actual = response.Content.Headers.ContentType?.ToString()?.Replace(" ", string.Empty);
        Assert.Equal(expected, actual);
    }
}
