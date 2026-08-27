# Bindery — Plan

A small self-hosted service that (1) serves an OPDS catalog and (2) acquires books
through downloader plugins. First plugin: [FanFicFare](https://github.com/JimmXinu/FanFicFare).

> **Name.** `Bindery` — a bindery is where loose sheets become a bound book, which is
> roughly the job. One `sed` away from being something else if you hate it.

---

## 1. Decisions already made

| Decision | Choice | Why |
|---|---|---|
| Host stack | ASP.NET Core 8, C# | DI, hosting, OIDC, `BackgroundService`, EF Core. Boring on purpose. |
| Core library | F# (`Bindery.Core`) | Feed shapes and protocol messages are algebraic data. DUs + exhaustive matching + no null is a real win for serialization code. |
| Web UI | Razor Pages + htmx | Server-rendered, no node build, no bundler. Also the thing that makes plugin UI passthrough work (§5). |
| Plugin transport | **HTTP only. Plugins are containers.** | No ALC, no subprocess, no plugin volume, no filesystem convention. |
| In-process plugins | **Dropped entirely** | A plugin never runs in the host's address space. Full stop. |
| Auth | OIDC day one (generic + Entra) for the UI; separate feed tokens for OPDS | Ereader apps cannot do an OAuth redirect. Two mechanisms is not optional. |
| Metadata store | SQLite via EF Core | One file, one PVC, no external dependency. |

### The consequence worth naming

Because plugins are containers, **the Bindery image contains no Python, ever.** It stays
a clean `aspnet` base. FanFicFare's dependency tree lives in FanFicFare's image, and
updating it — which you will do often, because fanfic sites break constantly — is a tag
bump in `values.yaml`, not a Bindery rebuild.

That is the whole argument for this architecture, and it is a good one.

---

## 2. Repository layout

```
bindery/
├── CLAUDE.md                      # working guidance for agents in this repo
├── PLAN.md                        # this file
├── README.md
├── docs/
│   ├── PLUGIN-PROTOCOL.md         # the contract. Normative. Versioned.
│   ├── PLUGIN-UI.md               # the three UI tiers (§5)
│   └── ARCHITECTURE.md
├── src/
│   ├── Bindery.Core/              # F# — domain, OPDS serialization, protocol DTOs
│   └── Bindery.Host/              # C# — ASP.NET Core: OPDS, API, UI, plugin client
├── tests/
│   ├── Bindery.Core.Tests/        # F#
│   └── conformance/               # black-box plugin conformance suite (Python)
├── plugins/
│   └── fanficfare/                # reference plugin: FastAPI service + Dockerfile
├── deploy/
│   ├── helm/bindery/
│   └── docker-compose.yml
├── Dockerfile
└── .github/workflows/
```

---

## 3. The plugin contract

A plugin is **an HTTP server in a container**. It is self-describing — there is no manifest
file to mount, no naming convention, no discovery magic. Bindery is told a base URL and
asks the plugin what it is.

All paths are under `/bindery/v1`. Full normative spec goes in `docs/PLUGIN-PROTOCOL.md`.

### Endpoints a plugin must serve

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/healthz` | Liveness. 200 = alive. |
| `GET` | `/bindery/v1/manifest` | Self-description: name, version, matchers, capabilities, config schema, UI mode. |
| `POST` | `/bindery/v1/probe` | `{url}` → `{supported, confidence}`. Optional; falls back to manifest regex matchers. |
| `POST` | `/bindery/v1/download` | The main event. Streams NDJSON (§3.2). |
| `GET` | `/bindery/v1/artifacts/{jobId}/{artifactId}` | Produced file bytes. |
| `DELETE` | `/bindery/v1/jobs/{jobId}` | Cancel if running, clean up artifacts. |

### 3.1 Manifest (sketch)

```json
{
  "protocolVersion": 1,
  "name": "fanficfare",
  "displayName": "FanFicFare",
  "version": "0.1.0",
  "homepage": "https://github.com/JimmXinu/FanFicFare",
  "priority": 100,
  "matches": [
    "^https?://(www\\.)?archiveofourown\\.org/works/\\d+",
    "^https?://(www\\.)?fanfiction\\.net/s/\\d+"
  ],
  "capabilities": { "update": true, "metadata": true, "cover": true },
  "config": [
    { "key": "ao3_username", "label": "AO3 username", "type": "string" },
    { "key": "ao3_password", "label": "AO3 password", "type": "secret" },
    { "key": "personal_ini", "label": "personal.ini overrides", "type": "text" }
  ],
  "ui": { "mode": "declarative" }
}
```

### 3.2 Download: NDJSON over a streaming response

`POST /bindery/v1/download` responds `200 application/x-ndjson` and streams events as it
works. Same message shapes a stdio protocol would have used — they just travel over a
socket. Client disconnect means cancel.

```jsonc
{"event":"progress","percent":40,"message":"chapter 4/10"}
{"event":"log","level":"info","message":"logged in to AO3"}
{"event":"result","status":"ok",
 "artifacts":[{"id":"a1","filename":"story.epub","format":"epub","primary":true,"bytes":481920}],
 "metadata":{"title":"…","authors":["…"],"series":"…","seriesIndex":1,
             "summary":"…","language":"en","tags":["…"],
             "sourceId":"ao3:12345","chapters":10,"updated":"2026-08-01T00:00:00Z"}}
```

Terminal `status` values: `ok`, `unchanged` (update found nothing new), `error`
(with `retryable: bool`).

Bindery then `GET`s each artifact and files it into the library. **Files move over HTTP.**
No shared volume, no RWX PVC — which many clusters don't have anyway.

### 3.3 Host → plugin auth

Shared bearer token from a k8s Secret, sent on every request. Sidecars are on localhost,
but `mode: service` plugins are not, and the contract shouldn't change between the two.

---

## 4. Deployment shapes

The Helm chart is the plugin installation mechanism. Installing a plugin is adding an entry
to `values.yaml`:

```yaml
plugins:
  - name: fanficfare
    enabled: true
    mode: sidecar                 # sidecar | service
    image: ghcr.io/OWNER/bindery-plugin-fanficfare:0.1.0
    port: 8080
    resources: { limits: { memory: 512Mi } }
    configSecret: fanficfare-creds
```

- **`mode: sidecar`** — rendered as an extra container in the Bindery pod, reached at
  `http://127.0.0.1:{port}`. Shared lifecycle, no network policy needed. The default.
- **`mode: service`** — its own Deployment + Service, reached by DNS. Scales and restarts
  independently.

The chart also renders a ConfigMap holding the plugin registry that Bindery reads at boot.
`deploy/docker-compose.yml` mirrors the same shape for local dev, so the mental model is
identical in both places.

---

## 5. Plugin UIs — three tiers

Plugins need to show things: credentials, `personal.ini`, site options, maybe a
search-and-pick browser. Design goal is that the common case costs a plugin author nothing
and carries no risk, while the escape hatch still exists.

The manifest declares which tier the plugin wants via `ui.mode`.

### Tier 1 — `declarative` (default, required to work)

The manifest's `config` array is a field schema. Bindery renders the form, validates it,
stores it, and passes values back on `download`. Plugin ships **zero HTML**.

Extended slightly beyond settings: a plugin may declare *actions* with input/output schemas
(`search` taking a query, returning a list of `{title, url, subtitle}`), and Bindery renders
a generic UI for them. That covers "search AO3 and pick a result" without a single tag of
plugin HTML.

Every plugin gets this for free whether or not it also ships HTML.

### Tier 2 — `sandboxed` (the plugin's own document) — **in v1**

A complete HTML document, served by the plugin into an iframe with an opaque origin.
Full contract in [`docs/PLUGIN-UI.md`](docs/PLUGIN-UI.md).

- Plugin serves `GET|POST /bindery/v1/ui/*` returning **full documents** plus its own
  subresources (`app.js`, `style.css`). The proxy is a byte pipe with a content-type
  allowlist and a size cap; it does not sanitize or rewrite anything.
- Bindery loads it into `<iframe sandbox="allow-scripts allow-forms allow-popups
  allow-downloads">` — note the absent `allow-same-origin`. That single omission is what
  assigns the document an opaque origin.
- **The plugin may ship whatever JavaScript it likes.** It cannot read Bindery's cookies,
  DOM, or storage, and `connect-src 'none'` removes `fetch`/XHR/WebSocket entirely.
- All host communication goes through a typed `postMessage` bridge: `request`/`response`
  scoped to the plugin's own mount point, plus `resize`, `ready`, `notify`, `navigate`.
  Bindery attaches the CSRF token; the plugin is never given it and so cannot leak it.
- Bindery's stylesheet is public API, so a plugin can link `/css/bindery.css` and inherit
  light/dark mode.

**Why this shape rather than sanitizing markup into Bindery's origin:** a fragment carries
content *and* authority in one blob, and a sanitizer is the allowlist parser that tries to
separate them — on every input, forever, without ever being wrong. Isolating instead means
there is no authority in reach for injected script to abuse, so nothing has to be perfect.
The security-critical surface becomes a ~100-line bridge with a five-item allowlist instead
of a 14 KB HTML parser, and the browser enforces the boundary rather than a regex.

### Tier 3 — `fragment` (deprecated)

HTML fragments swapped into the Bindery shell by htmx, sanitized on arrival. Still
supported, still enforced — a `fragment` plugin ships no JavaScript, no CSS, and builds
every URL from `X-Bindery-Base` — and the XSS conformance corpus remains a merge gate. The
reference FanFicFare plugin still uses it.

Deprecated because its safety rests on the sanitizer being perfect and `sandboxed` does not
need anything to be perfect. Removing it would be a breaking change and a `protocolVersion`
bump; that is a later decision, not a side effect of adding Tier 2.

### Build order

**Tiers 1 and 2 both ship in v1.** Tier 1 is nearly free once the manifest schema exists.
Tier 2 needs the reverse proxy, the sanitizer, the CSP, the CSRF wrapper, and the design
tokens — call it a phase of its own (§9 phase 4b). The FanFicFare plugin exercises it with a
`personal.ini` editor and an AO3 search-and-pick browser, so the path is proven by a real
consumer rather than a stub.

Tier 3 is specified but unbuilt until something demands it.

---

## 6. OPDS surface

Serve both. OPDS 1.2 is what ereaders actually implement; 2.0 is where things are going.

| Path | Feed |
|---|---|
| `/opds` | Root navigation feed (1.2 Atom XML) |
| `/opds/new`, `/opds/all` | Acquisition feeds, paginated |
| `/opds/authors`, `/opds/series`, `/opds/tags` | Navigation → acquisition |
| `/opds/search?q=` | Search results |
| `/opds/opensearch.xml` | OpenSearch description |
| `/opds/download/{bookId}.{ext}` | Acquisition link target |
| `/opds/cover/{bookId}`, `/opds/thumb/{bookId}` | Images |
| `/opds/v2/*` | Same tree, OPDS 2.0 JSON |

Content types matter and are easy to get wrong:
`application/atom+xml;profile=opds-catalog;kind=navigation` vs `kind=acquisition`.
`Bindery.Core` owns this; it is unit-tested against captured reference feeds.

**Verification target:** feeds must work in KOReader, Moon+ Reader, and Thorium. These
clients are quietly picky, and validating against the spec is not the same as working.

---

## 7. Data model

- `Book` — title, sort title, summary, language, published, added, updated, series +
  index, source URL, source plugin, source id, chapter count, cover path, content hash
- `Author`, `Tag`, `Series` + join tables
- `BookFile` — book, format, path, size, sha256
- `DownloadJob` — url, plugin, status, percent, message, timestamps, attempts, error, book
- `PluginSetting` — plugin, key, value (secrets encrypted at rest via Data Protection)
- `FeedToken` — name, hash, owning subject, created, last used, revoked

Library on disk: `/library/{Author}/{Title}/{file}.epub`, cover alongside. Human-browsable
and rsync-able on purpose — the database is an index, not the source of truth. If the DB is
lost, a rescan rebuilds it.

---

## 8. Auth

- **Web UI** — OIDC authorization code + PKCE. Config is generic (authority, client id,
  secret, scopes) so Entra, Authentik, Keycloak, and Dex all work. Cookie session after.
- **OPDS feeds** — long-lived feed tokens, issued per-device from the UI, stored hashed.
  Accepted as `Authorization: Bearer` *and* as HTTP Basic password, because that is what
  ereader apps can actually send.
- **Host → plugin** — shared bearer token.
- **Dev escape hatch** — `Auth:Mode=none` for local work, loud warning at boot.

---

## 9. Build phases

| Phase | Deliverable | Verifiable how |
|---|---|---|
| 0 | `docs/PLUGIN-PROTOCOL.md` + conformance suite | Suite runs against a stub plugin |
| 1 | FanFicFare plugin (Python/FastAPI + Dockerfile) | `curl` it directly; download a real story; conformance suite passes |
| 2 | `Bindery.Core` — OPDS 1.2/2.0, protocol types | Unit tests vs. reference feeds |
| 3 | `Bindery.Host` — EF Core, library, plugin client, download queue, OPDS endpoints | Integration test with stub plugin |
| 4a | Razor + htmx UI, OIDC, feed tokens | Manual + real ereader clients |
| 4b | Plugin UI passthrough: proxy, sanitizer, CSP, CSRF wrapper, design tokens | XSS vector suite; FanFicFare's own UI as the consumer |
| 5 | Dockerfile, compose, Helm chart, GHCR CI | `helm template` + `helm lint`; compose up |
| 6 | README, docs, polish | — |

Phase 0 first is deliberate: the contract is the product. Both sides get written against a
document, and the conformance suite means a third-party plugin can prove itself without
running Bindery at all.

---

## 10. Explicitly out of scope for v1

Update scheduler (re-fetch for new chapters) · watch folder / bulk import · read-progress
sync · multi-user libraries · calibre-style metadata editing · full-text search ·
plugin marketplace or registry.

The update scheduler is the strongest candidate for v1.1 — for ongoing fanfic it is
arguably the point — but it needs per-book source tracking to be solid first, and that's
what phase 3 builds.

---

## 11. Known risks

1. **OPDS client compatibility.** Spec-valid ≠ works in KOReader. Test on real clients early.
2. **Plugin HTML trust (§5 Tier 2) — now the biggest risk in v1.** The sanitizer is
   security-critical code sitting between untrusted markup and an authenticated session.
   Use a maintained library, keep the no-plugin-JS rule absolute, and make the XSS vector
   suite a merge gate. If the sanitizer ever feels inconvenient, the answer is Tier 3, not
   a loosened allowlist.
3. **Long downloads vs. HTTP timeouts.** A large multi-chapter fetch can run minutes.
   Streaming NDJSON keeps the connection warm, but ingress and service-mesh idle timeouts
   need explicit configuration in the chart.
4. **Secret handling.** Plugin config holds site passwords. Encrypted at rest, never logged,
   never echoed back to the UI in cleartext.
5. **FanFicFare is a moving target.** Pin the version in the plugin image; let Dependabot
   bump it. This is precisely the coupling the sidecar architecture exists to break.
