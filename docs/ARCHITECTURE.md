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
| `Library/` | `LibraryStore` writes and reads the book directory; `LibraryScanner` reindexes it; `ShelfService` says whose shelf each book is on |
| `Catalog/` | Paging, search and grouping queries behind both the UI and the feeds |
| `Downloads/` | The queue, the worker, and the runner that drives one job to completion |
| `Plugins/` | Registry, resolver, HTTP client, settings store, UI proxy |
| `Opds/` | The feed endpoints and URL construction |
| `Endpoints/` | The JSON API used by the UI |
| `Security/` | OIDC setup, roles and the user directory, feed tokens, fragment sanitizer, security headers |
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
   the index. Which book it lands on is decided by its *sources* (below): a job aimed at a
   book, then any book already fetched from that URL by any plugin, then — once the plugin
   has named the work — any book with that `sourceId`. Files a refresh no longer names (a
   new title means a new file name) are removed.
6. `DELETE /bindery/v1/jobs/{id}` tells the plugin to forget the job. Best effort: the
   protocol makes plugins expire artifacts on their own.

Failures carry a `retryable` flag. Retryable ones come back with exponential backoff up to
`MaxAttempts`; the rest stop and say why.

## Sources, duplicates, and merging

A book can be fetched from several places — Royal Road through FanFicFare, the author's own
WordPress site, an advance copy — and each plugin names it its own way.

- **Sources.** Every `(plugin, URL)` a book has been fetched from is a `BookSourceEntity`,
  mirrored in the sidecar's `sources` so a rescan rebuilds them. Exactly one is
  **followed**: `Book.SourcePlugin`/`SourceUrl`, which the update scheduler, the book page's
  "Check source", and plugin change-hints (protocol §3.7) all use. Fetching any other source
  replaces the files once and leaves following alone; following one fetches from it, so the
  files come from where the book is followed.
- **Asking for "nothing new".** `knownUpdated` is sent only when the book's current files
  came from that same source. Otherwise a plugin could answer `unchanged` and the book would
  silently keep another source's copy.
- **Identity** is `Bindery.Core.WorkIdentity`, a pure function. A shared source URL or source
  id is *certain*, and a download is filed into that book without asking. A matching title
  (stronger with a shared author) is only *probable*: the book is filed separately, the job
  log says what it resembles, and the book page offers a merge. Two serials can share a
  title; merging is destructive; so a person decides.
- **Merging** folds one book into another. The target keeps its id — so OPDS entries, read
  progress, and download history stay attached — and gains every source the other had. The
  person picks whose files and description survive, and whether to follow the other book's
  source. Files move first (copied, then the other directory deleted), the index last.
- **Shelves.** Sources, duplicate suggestions, and merges are scoped to the person's own
  shelf; another person's book is "not found". Merging needs both books on your shelf, and
  if someone else also holds the other book, only your claim on it is folded in — their copy
  stays. Only "is this source already in the library?" is asked library-wide, which is how
  a download lands on the shared copy instead of beside it.

## Serving the catalog

`/opds` is Atom, `/opds/v2` is OPDS 2.0, from the same feed model. The content type is
load-bearing — `kind=navigation` versus `kind=acquisition` decides how an ereader treats a
response, and real clients break when it is wrong, so it is asserted in tests on both
sides.

Authentication has two doors. The UI uses OIDC and a session cookie. Ereaders cannot do a
redirect flow, so feed endpoints also accept a long-lived feed token; tokens are stored
hashed and compared in constant time.

## People and shelves

Files are shared; views are not. One book directory holds each work once, and a *shelf*
(`ShelfEntries`: owner × book) says who holds it. Every catalog query, feed, cover, file
download, and progress record is filtered through the asking person's shelf, so another
person's book is simply a 404 — not a 403 that would confirm it exists. A feed token reads
its owner's shelf and nothing else.

Shelves are also written into each book's sidecar as `owners`, so the database stays a
rebuildable index: a rescan restores them. A book with no owner recorded (anything filed
before shelves existed) is adopted by the founder — the earliest person ever seen — at boot
and on that person's first sign-in.

Withdrawing a book removes it from your shelf; the files are deleted only when the last
owner lets go. A download of something already in the library adds it to the requester's
shelf without touching the files. Jobs belong to whoever queued them; an unattended update
belongs to nobody and is visible to everyone holding that book.

Admins (`Auth:AdminClaim`/`AdminValues`, evaluated on every request against the group
claims issued at sign-in — so a config change applies at once, but removing someone from
the group at the identity provider applies when they next sign in or their session ends) manage plugins and their installation settings,
rescans, and admin-only plugin actions and screens. Plugins are told who is acting by
`X-Bindery-User` — an HMAC pseudonym, distinct per plugin, never the OIDC subject — and
`X-Bindery-Role` (protocol §2.3).

## Plugin UI

Three tiers, declared by the manifest's `ui.mode`:

- `declarative` — Bindery renders forms and actions from the manifest's schema. Every
  plugin gets this for nothing.
- `sandboxed` — the plugin serves a whole document into an opaque-origin iframe and may
  ship its own JavaScript. Shipping in v1; the tier new plugins should use.
- `fragment` — the plugin serves HTML, reverse-proxied at `/plugins/{name}/ui/*`, sanitized,
  and swapped in by htmx. Deprecated, still supported.

For `sandboxed`, the load-bearing details are:

- **`<iframe sandbox="allow-scripts allow-forms allow-popups allow-downloads">` with no
  `allow-same-origin`.** That omission assigns the document an opaque origin, which is what
  denies it Bindery's cookies, DOM, storage, and API. It is the tier.
- **The proxy does not sanitize.** `PluginUiProxy.ForwardDocumentAsync` is a byte pipe with
  a content-type allowlist and a size cap. Script surviving intact is the point.
- **`SecurityHeaders.BuildFrameContentSecurityPolicy`** replaces the page policy for that
  response: `connect-src 'none'` (so the bridge is the only egress), `frame-ancestors 'self'`
  (replacing the global `X-Frame-Options: DENY`), and `'unsafe-inline'` — which is harmless
  in a context that holds no authority.
- **`wwwroot/js/plugin-frame.js` is the security-critical surface.** It validates messages
  by `event.source`, scopes every `request` to `/plugins/{name}/ui/`, normalizes with
  `new URL` before checking the prefix, and attaches the CSRF token the frame never sees.

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
  which fetches the same URL back with htmx. A sandboxed plugin is never redirected — the
  iframe's `src` is that URL.
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
