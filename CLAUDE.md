# CLAUDE.md

Guidance for Claude Code (and other agents) working in this repository.
Read `PLAN.md` for the architecture and roadmap. This file is about *how to work here*.

---

## What this is

Bindery serves an OPDS catalog and acquires books through downloader plugins.
ASP.NET Core 8 host (C#), F# core library, Razor Pages + htmx UI, SQLite via EF Core.

---

## The one rule that shapes everything

**A plugin is a container that speaks HTTP. It never runs in the host's address space.**

There is no `AssemblyLoadContext` loader, no subprocess spawning, no plugin directory to
mount, no `plugin.yaml` on disk. If you find yourself writing `Process.Start`,
`Assembly.LoadFrom`, or a filesystem scan for plugins, stop — that is a design that was
considered and explicitly rejected.

Corollaries that are easy to violate by accident:

- **The Bindery image must never contain Python, calibre, or any downloader dependency.**
  Base image stays `mcr.microsoft.com/dotnet/aspnet`. If a change adds `apt-get install
  python3` to the root `Dockerfile`, it is wrong.
- **No shared volume between host and plugin.** Artifacts move over HTTP via
  `GET /bindery/v1/artifacts/{jobId}/{artifactId}`. Do not reach for an RWX PVC.
- **Plugins are discovered from configuration, never by scanning.** The Helm chart renders
  a ConfigMap; the host reads it at boot.

## Repository layout

```
src/Bindery.Core/       F#  — domain types, OPDS 1.2/2.0 serialization, protocol DTOs
src/Bindery.Host/       C#  — ASP.NET Core: OPDS endpoints, JSON API, Razor UI, plugin client
tests/Bindery.Core.Tests/   F# unit tests
tests/conformance/          Python black-box suite any plugin image must pass
plugins/fanficfare/         Reference plugin: FastAPI service + Dockerfile
deploy/helm/bindery/        Helm chart — also the plugin install mechanism
deploy/docker-compose.yml   Local dev, mirrors the chart's shape
docs/PLUGIN-PROTOCOL.md     Normative contract. Change this before changing code.
```

### Which language goes where

F# (`Bindery.Core`) owns things that are *data with shape*: feed entry types, protocol
messages, metadata records, and their serialization. Discriminated unions and exhaustive
matching are why it exists.

C# (`Bindery.Host`) owns things that are *plumbing*: DI, hosting, EF Core, HTTP, auth,
background services, Razor.

When adding code, ask which of those two it is. Do not drift OPDS serialization into the
host, and do not drift DI wiring into the core.

## Build and test

```bash
dotnet build Bindery.sln
dotnet test  Bindery.sln
helm lint     deploy/helm/bindery
helm template deploy/helm/bindery          # renders sidecar containers from values.plugins
docker compose -f deploy/docker-compose.yml up --build
```

Plugin conformance (works against any plugin image, Bindery not required):

```bash
python tests/conformance/run.py --base-url http://localhost:8080
```

Hedgerow's chapter parser and store are stdlib-only and unit tested without the container:

```bash
cd plugins/hedgerow && python -m unittest discover -s tests -t .
```

## Conventions

- File-scoped namespaces, nullable enabled, `TreatWarningsAsErrors` off but keep it clean.
- Minimal APIs grouped in `Endpoints/*.cs` as extension methods; don't put logic in them.
- Every OPDS response's `Content-Type` matters —
  `application/atom+xml;profile=opds-catalog;kind=navigation` vs `kind=acquisition`. There
  are tests for this. They are not bureaucracy; ereaders genuinely break on it.
- Secrets (plugin site passwords, OIDC client secret, feed tokens) are encrypted at rest,
  never logged, and never rendered back into HTML in cleartext.
- Feed tokens are stored hashed. Compare in constant time.
- Library files on disk are the source of truth; SQLite is a rebuildable index. Never write
  a migration that makes the DB unrecoverable from a rescan.

## Protocol changes

`docs/PLUGIN-PROTOCOL.md` is normative and versioned (`protocolVersion` in the manifest).
The order is: update the spec → update the conformance suite → update `Bindery.Core` types →
update the host → update the reference plugin. Not the other way round.

Breaking the protocol means bumping `protocolVersion` and stating the migration in the spec.

## Plugin UIs

Three tiers, declared by the plugin's manifest (`ui.mode`) — see `PLAN.md` §5 and
`docs/PLUGIN-UI.md`.

- `declarative` — Bindery renders forms and actions from the manifest schema. Plugin ships
  no HTML. Every plugin gets this for free.
- `sandboxed` — plugin serves a complete document into an iframe with an opaque origin.
  **Shipping in v1. This is the tier new plugins should use.**
- `fragment` — plugin serves HTML fragments, reverse-proxied and sanitized into Bindery's
  own origin. **Deprecated but fully supported**; the reference FanFicFare plugin uses it.

### `sandboxed` — plugin JavaScript is allowed, and that is the point

The isolation is what buys it, so do not erode the isolation:

1. **Never add `allow-same-origin` to the iframe.** That one token is the entire tier. With
   it, the plugin's document shares Bindery's origin and every guarantee here evaporates —
   it becomes a same-origin XSS surface with a friendlier name. There is a test asserting
   its absence; if it ever fails, treat it as a security incident, not a broken test.
2. **The proxy does not sanitize in this mode, deliberately.** Do not "helpfully" run
   fragments' sanitizer over a sandboxed document. Script surviving intact is the feature.
3. **`connect-src 'none'` is load-bearing.** It is what makes the postMessage bridge the
   *only* egress rather than the recommended one. Do not relax it to let a plugin call
   `fetch`; that is a request to reintroduce ambient authority.
4. **The bridge is now the security-critical surface** (`wwwroot/js/plugin-frame.js`). Keep
   it small, non-parsing, and allowlist-driven. Every `request` path is validated to sit
   under that plugin's own `/plugins/{name}/ui/` mount, normalized with `new URL` *before*
   the prefix check. Inbound messages are identified by `event.source`, never by
   `event.origin` — an opaque origin is the string `"null"` and several frames would all
   claim it.
5. **The CSRF token stays in the parent.** The frame is never given it, so a plugin cannot
   leak it and cannot get CSRF wrong.

### `fragment` — unchanged, still enforced

Everything that was true of this tier is still true, in roughly the order it breaks things:

1. **A fragment plugin ships no JavaScript.** No inline, no `<script src>`, no `on*`, no
   `javascript:`. It uses Bindery's htmx (`hx-*` passes the sanitizer) for interactivity.
   If a plugin needs its own JS, the answer is `sandboxed` — never a loosened allowlist.
2. **The sanitizer is security-critical.** Use the maintained `HtmlSanitizer` library. Do
   not hand-roll it, do not disable it "temporarily," do not add an allowlist entry without
   a test. The XSS vector suite in `tests/conformance/` is a merge gate.
3. **Bindery does not rewrite attributes.** Plugins build URLs from the `X-Bindery-Base`
   request header. If you catch yourself regexing `hx-get` values, stop.
4. **CSRF is Bindery's job, not the plugin's.** The proxy wraps fragments in a container
   carrying `hx-headers` with the antiforgery token. Plugins do nothing.
5. Proxy forwards `text/html` only, unbuffered (plugins may stream SSE), size- and
   time-capped, `no-store`. Plugin UI requires a session — feed tokens are OPDS-only.

## Development environment gotchas

- **Restricted-network sandboxes:** some environments block `nuget.org` and all Microsoft
  hosts. `NuGet.offline.config` (gitignored) points at the SDK's bundled
  `FSharp/library-packs` so framework-only projects still build. Anything with a
  `PackageReference` will not restore there — build those in CI.
- The Ubuntu-packaged SDK is 8.0.x; the repo targets `net8.0` deliberately so it builds
  in more places.
- Docker may be unavailable (no daemon) in agent sandboxes. Validate Dockerfiles by
  inspection and let CI build them.

## Things not to do

- Don't add a NuGet dependency without a reason worth stating in the PR.
- Don't add a Node/npm build step. The UI is server-rendered on purpose; htmx is vendored.
- Don't implement the update scheduler, watch folder, or read-progress sync yet — see
  `PLAN.md` §10. They're deferred deliberately, not forgotten.
- Don't make the host aware of any specific plugin. There must be no `if (plugin ==
  "fanficfare")` anywhere in `src/`.
