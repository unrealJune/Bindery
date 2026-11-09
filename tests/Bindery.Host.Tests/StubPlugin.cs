using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Bindery.Host.Tests;

/// <summary>
/// A plugin, over real HTTP, in the test process.
/// </summary>
/// <remarks>
/// It is a separate Kestrel server on a loopback port rather than an in-memory handler,
/// because the thing under test is the transport: a plugin is a container that speaks
/// HTTP, and a test that stubs out the socket would not be testing the arrangement Bindery
/// actually ships. The wire shapes mirror
/// <c>tests/conformance/stub/stub_plugin.py</c> — that file is the reference; this is the
/// same protocol expressed where the host tests can reach it.
/// </remarks>
public sealed class StubPlugin : IAsyncDisposable
{
    public const string SupportedUrl = "https://stub.invalid/works/7";
    public const string UpdatedAt = "2026-08-01T00:00:00Z";

    private readonly WebApplication _app;
    private readonly Dictionary<string, Dictionary<string, Artifact>> _jobs = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    private StubPlugin(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    public string BaseUrl { get; }

    public string Name { get; init; } = "stub";

    /// <summary>While set, the manifest answers 503, as a sidecar still starting would.</summary>
    public volatile bool ManifestDown;

    /// <summary>Requests seen, so a test can assert on what the host actually sent.</summary>
    public List<string> Seen { get; } = [];

    /// <summary>
    /// Who each request said it was acting for (protocol §2.3), and the config it carried —
    /// so a test can assert one person's settings never reach another person's call.
    /// </summary>
    public ConcurrentQueue<Call> Calls { get; } = new();

    public sealed record Call(string Request, string? User, string? Role, JsonObject? Config);

    /// <summary>Set to have every download fail with a retryable error.</summary>
    public bool FailDownloads { get; set; }

    public bool FailSearch { get; set; }

    // ------------------------------------------------------------ circulation (protocol 3.8)

    /// <summary>The PIN the stub library accepts. Anything else is auth_required.</summary>
    public const string GoodPin = "2468";

    /// <summary>
    /// What the stub library says each card currently has, keyed by card number. Tests edit
    /// these between syncs to walk a hold through its lifecycle.
    /// </summary>
    public Dictionary<string, List<object>> CardHolds { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, List<object>> CardLoans { get; } = new(StringComparer.Ordinal);

    /// <summary>Set to make snapshots fail the way a half-loaded account page would.</summary>
    public bool FailSnapshots { get; set; }

    /// <summary>Hold operations the stub has been asked to perform, as "holdId:operation".</summary>
    public List<string> HoldOperations { get; } = [];

    /// <summary>The fragment the UI endpoint returns. Tests set hostile markup here.</summary>
    public string Fragment { get; set; } =
        """<div class="bnd-card"><h2>Stub</h2><button class="bnd-btn" hx-get="/plugins/stub/ui/more">More</button></div>""";

    /// <summary>
    /// The document a sandboxed plugin returns. It carries a <c>&lt;script&gt;</c> and an
    /// inline handler on purpose: in this tier they must survive untouched, which is the
    /// opposite of what the fragment tier asserts.
    /// </summary>
    public string Document { get; set; } =
        """
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><title>Stub</title></head>
        <body onload="boot()"><main id="app">stub</main><script src="app.js"></script></body></html>
        """;

    public string Script { get; set; } =
        """parent.postMessage({ bindery: 1, type: "ready" }, "*");""";

    public static async Task<StubPlugin> StartAsync(string uiMode = "fragment")
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var app = builder.Build();
        StubPlugin? plugin = null;

        app.Use(async (context, next) =>
        {
            var request = $"{context.Request.Method} {context.Request.Path}{context.Request.QueryString}";
            plugin!.Seen.Add(request);

            JsonObject? config = null;

            if (context.Request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
            {
                context.Request.EnableBuffering();

                using (var reader = new StreamReader(context.Request.Body, leaveOpen: true))
                {
                    var text = await reader.ReadToEndAsync();

                    try
                    {
                        config = (JsonNode.Parse(text) as JsonObject)?["config"]?.DeepClone() as JsonObject;
                    }
                    catch (JsonException)
                    {
                    }
                }

                context.Request.Body.Position = 0;
            }

            plugin.Calls.Enqueue(new Call(
                request,
                context.Request.Headers["X-Bindery-User"].FirstOrDefault(),
                context.Request.Headers["X-Bindery-Role"].FirstOrDefault(),
                config));

            await next();
        });

        app.MapGet("/healthz", () => Results.Json(new { status = "ok" }));

        app.MapGet("/bindery/v1/manifest", () => plugin!.ManifestDown
            ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
            : Results.Text(Manifest(plugin!.Name, uiMode), "application/json"));

        app.MapPost("/bindery/v1/probe", async (HttpContext context) =>
        {
            var body = await ReadJsonAsync(context);
            var url = body?["url"]?.GetValue<string>() ?? string.Empty;
            var supported = url.StartsWith("https://stub.invalid/works/", StringComparison.Ordinal);

            return Results.Json(new { supported, confidence = supported ? 1.0 : 0.0 });
        });

        app.MapPost("/bindery/v1/download", (HttpContext context) => plugin!.DownloadAsync(context));

        app.MapGet("/bindery/v1/artifacts/{jobId}/{artifactId}", (string jobId, string artifactId) =>
        {
            lock (plugin!._gate)
            {
                if (!plugin._jobs.TryGetValue(jobId, out var artifacts)
                    || !artifacts.TryGetValue(artifactId, out var artifact))
                {
                    return Results.NotFound();
                }

                return Results.Bytes(artifact.Data, artifact.ContentType, artifact.FileName);
            }
        });

        app.MapDelete("/bindery/v1/jobs/{jobId}", (string jobId) =>
        {
            lock (plugin!._gate)
            {
                plugin._jobs.Remove(jobId);
            }

            return Results.NoContent();
        });

        app.MapPost("/bindery/v1/actions/{action}", async (string action, HttpContext context) =>
        {
            var body = await ReadJsonAsync(context);
            var input = body?["input"];

            return action switch
            {
                "echo" => Results.Json(new
                {
                    status = "ok",
                    output = new { kind = "text", text = input?["text"]?.GetValue<string>() ?? string.Empty }
                }),
                "purge" => Results.Json(new { status = "ok", output = new { kind = "text", text = "purged" } }),
                "admin_search" => Results.Json(new { status = "ok", output = new { kind = "list", items = Array.Empty<object>() } }),
                "search" when plugin!.FailSearch => Results.Json(new
                {
                    status = "error",
                    error = new { code = "network", message = "Stub search unavailable", retryable = true }
                }),
                "search" => Results.Json(new
                {
                    status = "ok",
                    output = new
                    {
                        kind = "list",
                        items = new[]
                        {
                            new { title = "Stub result", subtitle = "fabricated", url = SupportedUrl }
                        }
                    }
                }),
                _ => Results.NotFound()
            };
        });

        app.MapPost("/bindery/v1/circulation/{**route}", (string route, HttpContext context) => plugin!.CirculationAsync(route, context));

        app.Map("/bindery/v1/ui/{**rest}", (HttpContext context, string? rest) =>
        {
            // A sandboxed plugin serves its own subresources through the same mount point,
            // and the proxy is what decides whether a content type may be forwarded at all.
            if (rest is not null && rest.EndsWith("app.js", StringComparison.Ordinal))
            {
                return Results.Content(plugin!.Script, "text/javascript", Encoding.UTF8);
            }

            if (rest is not null && rest.EndsWith("forbidden.bin", StringComparison.Ordinal))
            {
                return Results.Bytes([1, 2, 3], "application/octet-stream");
            }

            return Results.Content(
                uiMode == "sandboxed" ? plugin!.Document : plugin!.Fragment,
                "text/html",
                Encoding.UTF8);
        });

        await app.StartAsync();

        var address = app.Urls.First();
        plugin = new StubPlugin(app, address);

        return plugin;
    }

    private async Task DownloadAsync(HttpContext context)
    {
        var body = await ReadJsonAsync(context);
        var url = body?["url"]?.GetValue<string>() ?? string.Empty;
        var jobId = body?["jobId"]?.GetValue<string>() ?? Guid.NewGuid().ToString("D");
        var update = body?["options"]?["update"]?.GetValue<bool>() ?? false;
        var knownUpdated = body?["options"]?["knownUpdated"]?.GetValue<string>();
        var config = body?["config"] as JsonObject;

        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = "application/x-ndjson";

        async Task EmitAsync(object payload)
        {
            await context.Response.WriteAsync(JsonSerializer.Serialize(payload) + "\n");
            await context.Response.Body.FlushAsync();
        }

        if (FailDownloads)
        {
            await EmitAsync(new
            {
                @event = "result",
                status = "error",
                error = new { code = "site_error", message = "the stub was told to fail", retryable = true }
            });

            return;
        }

        if (!url.StartsWith("https://stub.invalid/works/", StringComparison.Ordinal))
        {
            await EmitAsync(new
            {
                @event = "result",
                status = "error",
                error = new { code = "unsupported_url", message = $"not a stub URL: {url}", retryable = false }
            });

            return;
        }

        if (update && knownUpdated == UpdatedAt)
        {
            await EmitAsync(new { @event = "result", status = "unchanged" });
            return;
        }

        // Like a real site, a chapter link names the same work as the work link does, and
        // the plugin reports the canonical URL — not the one that was pasted.
        var workId = url["https://stub.invalid/works/".Length..].Split('/')[0];
        var canonical = $"https://stub.invalid/works/{workId}";
        var title = $"Stub Work {workId}";
        var author = config?["author_name"]?.GetValue<string>() ?? "A. Stub";

        await EmitAsync(new { @event = "log", level = "info", message = "fabricating" });
        await EmitAsync(new { @event = "progress", percent = 50.0, message = "chapter 1/2" });

        var epub = BuildEpub(title, author);

        var artifacts = new Dictionary<string, Artifact>(StringComparer.Ordinal)
        {
            ["book"] = new($"{title}.epub", "epub", "application/epub+zip", "book", true, epub)
        };

        lock (_gate)
        {
            _jobs[jobId] = artifacts;
        }

        await EmitAsync(new
        {
            @event = "result",
            status = "ok",
            artifacts = artifacts.Select(pair => new
            {
                id = pair.Key,
                filename = pair.Value.FileName,
                format = pair.Value.Format,
                contentType = pair.Value.ContentType,
                kind = pair.Value.Kind,
                primary = pair.Value.Primary,
                bytes = pair.Value.Data.Length,
                sha256 = Convert.ToHexString(SHA256.HashData(pair.Value.Data)).ToLowerInvariant()
            }),
            metadata = new
            {
                title,
                authors = new[] { author },
                series = "Stub Series",
                seriesIndex = 1,
                summary = "Fabricated by the host integration stub.",
                language = "en",
                tags = new[] { "Stub" },
                sourceId = $"stub:{workId}",
                sourceUrl = canonical,
                chapters = 2,
                updated = UpdatedAt
            }
        });
    }

    private async Task<IResult> CirculationAsync(string route, HttpContext context)
    {
        var body = await ReadJsonAsync(context);
        var fields = body?["account"]?["fields"] as JsonObject;

        if (fields is null)
        {
            return Results.Json(new { error = new { code = "internal", message = "'account' is required" } }, statusCode: 422);
        }

        var card = fields["card"]?.GetValue<string>() ?? string.Empty;
        var pin = fields["pin"]?.GetValue<string>();

        IResult Refuse(string code, string message) =>
            Results.Json(new { status = "error", error = new { code, message, retryable = false } });

        if (route == "search")
        {
            var medium = body?["medium"]?.GetValue<string>() ?? "any";
            var items = new object[]
            {
                new { id = "bib-1", title = "A Shelf Copy", authors = new[] { "Stub, A." }, medium = "physical",
                      format = "book", formatLabel = "Book", holdable = true,
                      availability = new { status = "unavailable", copies = 3, availableCopies = 0, holds = 5 } },
                new { id = "bib-2", title = "A Licence", authors = new[] { "Stub, A." }, medium = "digital",
                      format = "ebook", formatLabel = "eBook", provider = "StubDrive", holdable = false,
                      borrowUrl = "https://stub.invalid/borrow/bib-2",
                      availability = new { status = "available", copies = 1, availableCopies = 1, holds = 0 } }
            }.Where(item => medium == "any" || ((dynamic)item).medium == medium).ToArray();

            return Results.Json(new { status = "ok", total = items.Length, page = 1, pages = 1, items });
        }

        if (pin != GoodPin)
        {
            return Refuse("auth_required", "the stub library refused that PIN");
        }

        lock (_gate)
        {
            var holds = CardHolds.TryGetValue(card, out var h) ? h : CardHolds[card] = [];
            var loans = CardLoans.TryGetValue(card, out var l) ? l : CardLoans[card] = [];

            switch (route)
            {
                case "verify":
                    return Results.Json(new
                    {
                        status = "ok",
                        library = new { id = "stubville", name = "Stubville Public Library" },
                        displayName = $"card ending {card[^4..]}",
                        pickupLocations = new[] { new { id = "1", name = "Main Branch" }, new { id = "2", name = "Harbour Branch" } }
                    });

                case "snapshot" when FailSnapshots:
                    return Refuse("network", "the account page did not finish loading");

                case "snapshot":
                    return Results.Json(new { status = "ok", holds, loans });

                case "holds":
                    var itemId = body?["itemId"]?.GetValue<string>();

                    if (itemId != "bib-1")
                    {
                        return Refuse("not_holdable", "only bib-1 can be held");
                    }

                    holds.Add(new { id = $"hold-{holds.Count + 1}", itemId, title = "A Shelf Copy", medium = "physical", state = "pending", queuePosition = 5, queueSize = 5 });
                    return Results.Json(new { status = "ok" });

                default:
                    var parts = route.Split('/');

                    if (parts is ["holds", var holdId, var operation and ("cancel" or "suspend" or "resume")])
                    {
                        HoldOperations.Add($"{holdId}:{operation}");

                        if (operation == "cancel")
                        {
                            holds.RemoveAll(hold => ((dynamic)hold).id == holdId);
                        }

                        return Results.Json(new { status = "ok" });
                    }

                    return Results.NotFound();
            }
        }
    }

    private static async Task<JsonObject?> ReadJsonAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body);
        var text = await reader.ReadToEndAsync();
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text) as JsonObject;
    }

    private static byte[] BuildEpub(string title, string author)
    {
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
                     <dc:identifier id="pub-id">urn:bindery:stub</dc:identifier>
                     <dc:title>{WebUtility.HtmlEncode(title)}</dc:title>
                     <dc:creator>{WebUtility.HtmlEncode(author)}</dc:creator>
                     <dc:language>en</dc:language>
                   </metadata>
                   <manifest><item id="c1" href="chapter1.xhtml" media-type="application/xhtml+xml"/></manifest>
                   <spine><itemref idref="c1"/></spine>
                 </package>
                 """);
            Write(zip, "OEBPS/chapter1.xhtml", "<html><body><p>Fabricated.</p></body></html>");
        }

        return buffer.ToArray();
    }

    private static void Write(ZipArchive zip, string name, string content)
    {
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }

    private static string Manifest(string name, string uiMode) =>
        $$"""
          {
            "protocolVersion": 1,
            "name": "{{name}}",
            "displayName": "Conformance Stub",
            "version": "1.0.0",
            "priority": -100,
            "matches": ["^https?://stub\\.invalid/works/\\d+(/chapters/\\d+)?$"],
            "formats": ["epub"],
            "capabilities": { "probe": true, "update": true, "metadata": true, "cover": true, "cancel": true, "circulation": true },
            "circulation": {
              "accountFields": [
                { "key": "card", "label": "Card number", "type": "string", "required": true },
                { "key": "pin", "label": "PIN", "type": "secret", "required": true }
              ],
              "holdOperations": ["cancel", "suspend", "resume"]
            },
            "config": [
              { "key": "author_name", "label": "Author to report", "type": "string" },
              { "key": "api_key", "label": "Pretend API key", "type": "secret" },
              { "key": "reader_login", "label": "Your login", "type": "string", "scope": "user" }
            ],
            "actions": [
              { "name": "echo", "label": "Echo",
                "input": [{ "key": "text", "label": "Text", "type": "string", "required": true }],
                "output": { "kind": "text" } },
              { "name": "search", "label": "Search", "search": true,
                "input": [{ "key": "q", "label": "Query", "type": "string", "required": true }],
                "output": { "kind": "list", "itemAction": "download" } },
              { "name": "admin_search", "label": "Admin search", "search": true, "access": "admin",
                "input": [{ "key": "q", "label": "Query", "type": "string", "required": true }],
                "output": { "kind": "list", "itemAction": "download" } },
              { "name": "purge", "label": "Purge", "access": "admin", "input": [], "output": { "kind": "text" } }
            ],
            "ui": {
              "mode": "{{uiMode}}",
              "nav": [{ "label": "Stub", "path": "/" }, { "label": "Admin", "path": "/admin", "access": "admin" }],
              "adminPaths": ["/admin"]
            }
          }
          """;

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private sealed record Artifact(
        string FileName,
        string Format,
        string ContentType,
        string Kind,
        bool Primary,
        byte[] Data);
}
