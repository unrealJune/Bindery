using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace Bindery.Host.Tests;

/// <summary>
/// Putting a book on the shelf by hand, and taking one off it.
/// </summary>
/// <remarks>
/// These are the two paths that write to the library without a plugin, so they are also the
/// two that could most easily disagree with the download path about what a filed book looks
/// like. The assertions are therefore about the volume, not just the rows: an upload that
/// indexes but does not land on disk, or a delete that clears the index and leaves the
/// files, would each survive a database-only test and neither survives a rescan.
/// </remarks>
[Collection(BinderyCollection.Name)]
public sealed class ShelfTests(BinderyFixture fixture)
{
    [Fact]
    public async Task An_uploaded_epub_is_read_filed_and_indexed()
    {
        var bytes = BuildEpub("Uploaded Work", "Ada Uploader", series: "Deposit Cycle", seriesIndex: "2");

        using var response = await UploadAsync(bytes, "whatever-the-file-was-called.epub");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<UploadPayload>();
        Assert.NotNull(created);
        Assert.False(created.AlreadyHeld);

        // The metadata comes out of the file, so nothing had to be typed to file it.
        var book = await fixture.Client.GetFromJsonAsync<BookPayload>($"/api/books/{created.Id}");
        Assert.NotNull(book);
        Assert.Equal("Uploaded Work", book.Title);
        Assert.Equal("Deposit Cycle", book.Series);
        Assert.Contains("Ada Uploader", book.Authors);
        Assert.Contains(book.Files, file => file.Format == "epub" && file.SizeBytes > 0);

        // Nothing fetched it, so it must not claim a downloader or a source to refresh from.
        Assert.Null(book.SourcePlugin);
        Assert.Null(book.SourceUrl);

        // The file, under {Author}/{Title}, is the library. The row is the index of it.
        var expected = Path.Combine(fixture.LibraryPath, "Ada Uploader", "Uploaded Work");
        Assert.True(Directory.Exists(expected), $"expected the book at {expected}");
        Assert.True(File.Exists(Path.Combine(expected, "bindery.json")));
        Assert.NotEmpty(Directory.GetFiles(expected, "*.epub"));

        using var download = await fixture.Client.GetAsync($"/opds/download/{created.Id}.epub");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/epub+zip", download.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task An_upload_can_be_titled_by_hand_and_carries_its_cover()
    {
        var bytes = BuildEpub("Ignored Inner Title", "Inner Author", cover: true);

        using var response = await UploadAsync(
            bytes, "book.epub", title: "Stated Title", author: "Stated Author, Second Author");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<UploadPayload>();

        var book = await fixture.Client.GetFromJsonAsync<BookPayload>($"/api/books/{created!.Id}");
        Assert.NotNull(book);
        Assert.Equal("Stated Title", book.Title);
        Assert.Equal(["Stated Author", "Second Author"], book.Authors);
        Assert.True(book.HasCover);

        using var cover = await fixture.Client.GetAsync($"/opds/cover/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, cover.StatusCode);
    }

    [Fact]
    public async Task The_same_bytes_twice_is_recognised_rather_than_filed_twice()
    {
        var bytes = BuildEpub("Duplicated Work", "Twice Over");

        using var first = await UploadAsync(bytes, "one.epub");
        using var second = await UploadAsync(bytes, "another-name.epub");

        var one = await first.Content.ReadFromJsonAsync<UploadPayload>();
        var two = await second.Content.ReadFromJsonAsync<UploadPayload>();

        Assert.False(one!.AlreadyHeld);
        Assert.True(two!.AlreadyHeld);
        Assert.Equal(one.Id, two.Id);
    }

    [Fact]
    public async Task A_format_bindery_does_not_file_is_refused_with_a_reason()
    {
        using var response = await UploadAsync(Encoding.UTF8.GetBytes("not a book"), "notes.docx");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("docx", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deleting_a_book_removes_its_files_and_its_rows()
    {
        var bytes = BuildEpub("Withdrawn Work", "Gone Author");
        using var uploaded = await UploadAsync(bytes, "withdrawn.epub");
        var created = await uploaded.Content.ReadFromJsonAsync<UploadPayload>();

        var directory = Path.Combine(fixture.LibraryPath, "Gone Author", "Withdrawn Work");
        Assert.True(Directory.Exists(directory));

        using var deleted = await fixture.Client.DeleteAsync($"/api/books/{created!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        using var gone = await fixture.Client.GetAsync($"/api/books/{created.Id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);

        // The files really go: leaving them would have the next rescan resurrect the book.
        Assert.False(Directory.Exists(directory));

        // The author directory is emptied too rather than left as litter on the volume.
        Assert.False(Directory.Exists(Path.Combine(fixture.LibraryPath, "Gone Author")));

        using var scan = await fixture.Client.PostAsync("/api/library/scan", content: null);
        Assert.Equal(HttpStatusCode.OK, scan.StatusCode);

        using var stillGone = await fixture.Client.GetAsync($"/api/books/{created.Id}");
        Assert.Equal(HttpStatusCode.NotFound, stillGone.StatusCode);

        using var feed = await fixture.Client.GetAsync("/opds/all");
        Assert.DoesNotContain("Withdrawn Work", await feed.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_register_renders_and_names_the_downloader_each_book_came_from()
    {
        using var uploaded = await UploadAsync(BuildEpub("Badged Work", "Badge Author"), "badged.epub");
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);

        // Rendering the pages at all is half the point of this test: the register row is a
        // shared partial, and a partial that fails to resolve fails at runtime, not build.
        foreach (var path in new[] { "/", "/library" })
        {
            using var page = await fixture.Client.GetAsync(path);

            Assert.Equal(HttpStatusCode.OK, page.StatusCode);

            var html = await page.Content.ReadAsStringAsync();
            Assert.Contains("Badged Work", html, StringComparison.Ordinal);
            Assert.Contains("source-badge", html, StringComparison.Ordinal);

            // Nothing downloaded it, so the badge must say so rather than name a plugin.
            Assert.Contains("Manual", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Deleting_a_book_that_is_not_there_is_a_not_found()
    {
        using var response = await fixture.Client.DeleteAsync($"/api/books/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ------------------------------------------------------------ helpers

    private async Task<HttpResponseMessage> UploadAsync(
        byte[] bytes,
        string fileName,
        string? title = null,
        string? author = null)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        form.Add(file, "file", fileName);

        if (title is not null)
        {
            form.Add(new StringContent(title), "title");
        }

        if (author is not null)
        {
            form.Add(new StringContent(author), "author");
        }

        return await fixture.Client.PostAsync("/api/library/upload", form);
    }

    /// <summary>A minimal but genuine EPUB, so the reader has something real to read.</summary>
    private static byte[] BuildEpub(
        string title,
        string author,
        string? series = null,
        string? seriesIndex = null,
        bool cover = false)
    {
        var collection = series is null
            ? string.Empty
            : $"""
                   <meta property="belongs-to-collection" id="col">{WebUtility.HtmlEncode(series)}</meta>
                   <meta refines="#col" property="group-position">{seriesIndex ?? "1"}</meta>
               """;

        var coverItem = cover
            ? """<item id="pic" href="cover.png" media-type="image/png" properties="cover-image"/>"""
            : string.Empty;

        using var buffer = new MemoryStream();

        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "mimetype", "application/epub+zip");
            Write(
                zip,
                "META-INF/container.xml",
                """
                <?xml version="1.0" encoding="UTF-8"?>
                <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                  <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
                </container>
                """);
            Write(
                zip,
                "OEBPS/content.opf",
                $"""
                 <?xml version="1.0" encoding="UTF-8"?>
                 <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="pub-id">
                   <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                     <dc:identifier id="pub-id">urn:bindery:upload:{WebUtility.HtmlEncode(title)}</dc:identifier>
                     <dc:title>{WebUtility.HtmlEncode(title)}</dc:title>
                     <dc:creator>{WebUtility.HtmlEncode(author)}</dc:creator>
                     <dc:language>en</dc:language>
                 {collection}
                   </metadata>
                   <manifest>
                     <item id="c1" href="chapter1.xhtml" media-type="application/xhtml+xml"/>
                     {coverItem}
                   </manifest>
                   <spine><itemref idref="c1"/></spine>
                 </package>
                 """);
            Write(zip, "OEBPS/chapter1.xhtml", $"<html><body><p>{WebUtility.HtmlEncode(title)}</p></body></html>");

            if (cover)
            {
                using var stream = zip.CreateEntry("OEBPS/cover.png").Open();
                stream.Write(PngPixel);
            }
        }

        return buffer.ToArray();
    }

    private static void Write(ZipArchive zip, string name, string content)
    {
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }

    /// <summary>A 1x1 PNG. Small enough to inline, real enough to be served as one.</summary>
    private static ReadOnlySpan<byte> PngPixel => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private sealed record UploadPayload(Guid Id, string Title, bool AlreadyHeld);

    private sealed record BookPayload(
        Guid Id,
        string Title,
        IReadOnlyList<string> Authors,
        string? Series,
        string? SourceUrl,
        string? SourcePlugin,
        bool HasCover,
        IReadOnlyList<BookFilePayload> Files);

    private sealed record BookFilePayload(string Format, long SizeBytes);
}
