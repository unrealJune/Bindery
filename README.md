# Bindery

A small self-hosted service that serves an **OPDS catalog** and acquires books through
**downloader plugins**.

> **Status: pre-alpha.** The design is settled and written down; the code is being built.
> See [`PLAN.md`](PLAN.md) for the architecture and roadmap.

---

## What it does

- Serves your library as an OPDS 1.2 and OPDS 2.0 catalog, so any ereader app can browse
  and download from it — KOReader, Moon+ Reader, Thorium, Aldiko.
- Acquires new books by handing a URL to whichever plugin claims it.
- Ships as a Docker image and a Helm chart.

The first plugin wraps [FanFicFare](https://github.com/JimmXinu/FanFicFare): paste an AO3
or FFN link, get an EPUB in your catalog.

## The idea

**A plugin is a container that speaks HTTP.** Not a shared library, not a subprocess, not a
directory you mount. A plugin is a service that describes itself over an endpoint, and
Bindery talks to it over the network.

That one decision buys most of what makes this pleasant:

- **Bindery's image contains no Python.** It's a clean `aspnet` base. A downloader's
  dependency tree lives in the downloader's image.
- **Plugins version independently.** Fanfic sites break constantly and FanFicFare updates
  constantly. That's a tag bump in `values.yaml`, not a Bindery rebuild.
- **Installing a plugin is a Helm values entry.** No plugin volume, no naming convention,
  no discovery magic.
- **Plugins are isolated.** Per-plugin resource limits, network policy, and restart
  behaviour. A wedged downloader doesn't take the catalog down with it.
- **Plugins can be written in anything** that can serve HTTP in a container.

```yaml
plugins:
  - name: fanficfare
    enabled: true
    mode: sidecar                 # or: service
    image: ghcr.io/OWNER/bindery-plugin-fanficfare:0.1.0
    port: 8080
    configSecret: fanficfare-creds
```

## Plugins can bring their own UI

A plugin isn't limited to returning files — it can contribute real, server-rendered HTML to
the Bindery interface. Bindery reverse-proxies the plugin's fragments and htmx swaps them
into the page, so a plugin's screens sit inside Bindery's chrome and inherit its styling.

The rule that makes it safe: **a plugin ships no JavaScript.** It uses Bindery's htmx
runtime for interactivity — async forms, polling, SSE progress, lazy loading — which covers
essentially everything, while keeping the sanitizer's job small enough to be exhaustively
tested. Plugins that genuinely need their own JS declare `mode: "iframe"` and get sandboxed
on a separate origin instead.

Full contract: [`docs/PLUGIN-UI.md`](docs/PLUGIN-UI.md).

## Writing a plugin

Serve six endpoints and you're a plugin:

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/healthz` | liveness |
| `GET` | `/bindery/v1/manifest` | what you are, what URLs you claim, what config you need |
| `POST` | `/bindery/v1/probe` | optional — do you handle this URL? |
| `POST` | `/bindery/v1/download` | do the work; stream NDJSON progress events |
| `GET` | `/bindery/v1/artifacts/{jobId}/{artifactId}` | hand back the file |
| `DELETE` | `/bindery/v1/jobs/{jobId}` | cancel and clean up |

`POST /download` streams progress as it works, and finishes with a result event:

```jsonc
{"event":"progress","percent":40,"message":"chapter 4/10"}
{"event":"result","status":"ok",
 "artifacts":[{"id":"a1","filename":"story.epub","format":"epub","primary":true}],
 "metadata":{"title":"…","authors":["…"],"sourceId":"ao3:12345","chapters":10}}
```

The contract is normative and versioned in [`docs/PLUGIN-PROTOCOL.md`](docs/PLUGIN-PROTOCOL.md).
`tests/conformance/` will run against any plugin image — no Bindery required — so you can
prove a plugin works standalone.

## Stack

ASP.NET Core 8 (C#) for the host, F# for the core domain and OPDS serialization, Razor Pages
+ htmx for the UI, SQLite via EF Core for the index. No Node build step. The files on disk
are the source of truth; the database is a rebuildable index.

## Auth

OIDC (generic — Entra, Authentik, Keycloak, Dex) for the web UI. Separate long-lived feed
tokens for OPDS, because ereader apps can't do an OAuth redirect.

## Development

```bash
dotnet build Bindery.sln
dotnet test  Bindery.sln
docker compose -f deploy/docker-compose.yml up --build
helm template deploy/helm/bindery
```

See [`CLAUDE.md`](CLAUDE.md) for conventions and the constraints that are easy to violate
by accident.

## License

TBD.
