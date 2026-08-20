# Architecture

How Bindery is put together, and why. [`PLAN.md`](../PLAN.md) argues for the design;
this describes what was built. [`CLAUDE.md`](../CLAUDE.md) lists the rules that are easy
to break by accident.

---

## The shape of it

```
ereader ──OPDS──┐
                ├──► Bindery.Host ──HTTP──► plugin container
browser ──htmx──┘         │
                          ├──► SQLite (index)
                          └──► library directory (source of truth)
```

Three durable claims:

1. **A plugin is a container that speaks HTTP.** Not an assembly, not a subprocess, not a
   directory to scan. Bindery knows a plugin by a name and a base URL it was configured
   with, and everything else it learns by asking.
2. **Files on disk are the truth; SQLite is a rebuildable index.** Deleting the database
   loses history, not books — `LibraryScanner` rebuilds the rest from the files.
3. **Data with shape lives in F#; plumbing lives in C#.** Feed entries, protocol messages
   and metadata are discriminated unions with exhaustive matching; hosting, DI, EF Core,
   HTTP and Razor are not.

## Projects

| Project | Language | Owns |
|---|---|---|
| `src/Bindery.Core` | F# | Domain types, OPDS 1.2/2.0 serialization, protocol DTOs and their parsers |
| `src/Bindery.Host` | C# | ASP.NET Core host: endpoints, EF Core, background work, Razor UI, plugin transport |
| `tests/Bindery.Core.Tests` | F# | Unit tests over serialization and parsing |
| `tests/Bindery.Host.Tests` | C# | Integration tests over the whole host, against an in-process stub plugin |
| `tests/conformance` | Python | Black-box suite any plugin image must pass, with Bindery absent |
| `plugins/fanficfare` | Python | The reference plugin: FastAPI wrapping FanFicFare |

### Inside `Bindery.Core`

- `Domain.fs` — `Book`, `BookFile`, `Author`, `Grouping`, format and content-type tables.
- `Opds.fs` — feed and link models, the content-type constants ereaders route on, and the
  Atom and OPDS 2.0 renderers.
- `Protocol.fs` — manifest, probe, download request, NDJSON events, action outcomes, and
  the parsers that turn a plugin's claims into types. Parsing returns a result; a
  malformed manifest is a plugin bug, not an exception in the host.

### Inside `Bindery.Host`

| Area | What happens there |
|---|---|
| `Configuration/` | `BinderyOptions` — everything configurable, bound from `Bindery__*` |
| `Data/` | EF Core entities, `BinderyDbContext`, migrations |
| `Library/` | `LibraryStore` writes and reads the book directory; `LibraryScanner` reindexes it |
| `Catalog/` | Paging, search and grouping queries behind both the UI and the feeds |
| `Downloads/` | The queue, the worker, and the runner that drives one job to completion |
| `Plugins/` | Registry, resolver, HTTP client, settings store, UI proxy |
| `Opds/` | The feed endpoints and URL construction |
| `Endpoints/` | The JSON API used by the UI |
| `Security/` | OIDC setup, feed tokens, fragment sanitizer, security headers |
| `Pages/` | Razor Pages and htmx partials |

## The acquisition path

A download is the interesting path, because it crosses every seam:

1. `POST /api/downloads` with a URL. `PluginResolver` picks a plugin: an explicit choice,
   then a `probe` if the plugin offers one, then the manifest's URL patterns, with the
   manifest's `priority` breaking ties. Nothing is queued if nothing claims the URL.
2. A `DownloadJobEntity` is persisted and `DownloadQueue` is signalled. `DownloadWorker`
   runs jobs at the configured concurrency and also sweeps stale staging files at boot.
3. `DownloadRunner` calls `POST /bindery/v1/download` and reads NDJSON. `progress` and
   `log` events update the job — which is what the UI polls. Cancelling the job disposes
   the response, and a dropped connection *is* the cancel signal; there is no second
   handshake to fall out of sync.
4. The `result` event names artifacts. Each is fetched from
   `GET /bindery/v1/artifacts/{jobId}/{artifactId}` into a staging directory under
   `DataPath`, hashed while streaming, and rejected if it exceeds `MaxArtifactBytes` or
   fails its own declared digest. Size limits are enforced against what arrives, never
   against what the plugin claims.
5. `LibraryStore` files the artifact under `Author/Title/` and the book is upserted into
   the index, matched on `sourceId` so a re-download updates rather than duplicates.
6. `DELETE /bindery/v1/jobs/{id}` tells the plugin to forget the job. Best effort: the
   protocol makes plugins expire artifacts on their own.

Failures carry a `retryable` flag. Retryable ones come back with exponential backoff up to
`MaxAttempts`; the rest stop and say why.

## Serving the catalog

`/opds` is Atom, `/opds/v2` is OPDS 2.0, from the same feed model. The content type is
load-bearing — `kind=navigation` versus `kind=acquisition` decides how an ereader treats a
response, and real clients break when it is wrong, so it is asserted in tests on both
sides.

Authentication has two doors. The UI uses OIDC and a session cookie. Ereaders cannot do a
redirect flow, so feed endpoints also accept a long-lived feed token; tokens are stored
hashed and compared in constant time.

## Plugin UI

Three tiers, declared by the manifest's `ui.mode`:

- `declarative` — Bindery renders forms and actions from the manifest's schema. Every
  plugin gets this for nothing.
- `fragment` — the plugin serves HTML, reverse-proxied at `/plugins/{name}/ui/*` and
  swapped in by htmx. Shipping in v1.
- `iframe` — sandboxed on a separate origin, specified but unbuilt.

For `fragment`, the load-bearing details are:

- **The plugin ships no JavaScript.** `FragmentSanitizer` (the maintained `HtmlSanitizer`
  library, never hand-rolled) drops `<script>`, `on*`, and `javascript:` URLs, and permits
  `hx-*` so htmx remains the plugin's route to interactivity.
- **Bindery does not rewrite the plugin's attributes.** The plugin builds URLs from the
  `X-Bindery-Base` header it is given. Nothing regexes an `hx-get`.
- **CSRF is Bindery's job.** `PluginUiProxy.Wrap` puts the antiforgery token in
  `hx-headers` on the container, and htmx inherits it. Plugins do nothing.
- The proxy forwards `text/html` only, unbuffered so SSE works, size- and time-capped, and
  `no-store`. A plain browser GET is redirected to the plugin's page in the Bindery shell,
  which fetches the same URL back with htmx.
- Plugin UI requires a session. Feed tokens are OPDS-only.

## Configuration and deployment

There is no plugin directory and no discovery. The host reads its plugin registry from
configuration at boot:

```
Bindery__Plugins__Registry__0__Name=fanficfare
Bindery__Plugins__Registry__0__BaseUrl=http://127.0.0.1:8080
Bindery__Plugins__Registry__0__Enabled=true
```

The Helm chart renders exactly that from `values.plugins`, choosing the base URL from the
plugin's `mode`: `sidecar` gets `127.0.0.1`, `service` gets its own Deployment, Service and
DNS name. Installing a plugin is a values entry.

Plugin secrets — site passwords, API keys — are encrypted at rest with ASP.NET Core Data
Protection, whose keys live under `DataPath`. They are never logged and never rendered back
into HTML. Losing the keys means re-entering the secrets, which is the correct outcome.

The host image is `mcr.microsoft.com/dotnet/aspnet` and contains no downloader dependency —
no Python, no calibre. Plugins get no shared volume: artifacts move over HTTP, which is
what lets a plugin run as a separate Deployment, or on a different node, without anything
in Bindery changing.

## Testing

- `tests/Bindery.Core.Tests` — serialization and parsing against reference feeds.
- `tests/Bindery.Host.Tests` — the host end to end. The stub plugin is a second Kestrel
  server on a loopback port rather than a mocked handler, because the thing being tested
  is the transport; a test that stubs the socket would not exercise the arrangement that
  ships.
- `tests/conformance` — the plugin contract, black box, no Bindery required, including the
  XSS vector suite that gates fragment plugins.
