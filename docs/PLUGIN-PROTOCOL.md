# Bindery plugin protocol

**Status: normative. `protocolVersion: 1`.**

A Bindery plugin is an HTTP server in a container. It is self-describing: Bindery is given
a base URL and asks the plugin what it is. There is no manifest file to mount, no naming
convention, and no filesystem discovery.

This document is the contract. Both sides — the host and every plugin — are written
against it, and `tests/conformance/` verifies a plugin against it without Bindery running.

Requirement keywords (**MUST**, **MUST NOT**, **SHOULD**, **MAY**) are used in the RFC 2119
sense.

---

## 1. Versioning

The manifest carries `protocolVersion`, an integer. Version 1 is described here.

- A host **MUST** refuse to use a plugin whose `protocolVersion` it does not implement, and
  **MUST** surface that as a plugin-level error rather than failing a download.
- Additive changes (new optional fields, new event types, new error codes) do **not** bump
  the version. Consumers on both sides **MUST** ignore unknown object members and **MUST**
  ignore NDJSON events with an unrecognized `event` value.
- Removing or repurposing a field, or changing a required shape, bumps `protocolVersion` and
  the migration is documented here.

---

## 2. Transport

| | |
|---|---|
| Protocol | HTTP/1.1 over TCP. TLS is the deployment's concern, not the protocol's. |
| Base path | All protocol endpoints live under `/bindery/v1`. `/healthz` is the one exception. |
| Encoding | Request and response bodies are UTF-8. JSON bodies are `application/json`. |
| Streaming | `POST /bindery/v1/download` responds `application/x-ndjson` and streams. |

### 2.1 Authentication

Bindery sends a shared bearer token on every request under `/bindery/v1`:

```
Authorization: Bearer <token>
```

- A plugin configured with a token (`BINDERY_PLUGIN_TOKEN`, see §8) **MUST** reject requests
  to `/bindery/v1/*` whose token is missing or does not match with `401` and a JSON error
  body (§7).
- `/healthz` **MUST** remain reachable without the token. Orchestrator probes cannot
  reasonably carry one, and liveness reveals nothing.
- Token comparison **MUST** be constant-time.
- A plugin with no token configured **MAY** accept all requests. This is for local
  development only and the plugin **SHOULD** log a warning at startup.

The mechanism does not change between `mode: sidecar` (localhost) and `mode: service`
(cluster DNS). One contract, both shapes.

### 2.2 Headers Bindery sends

| Header | On | Meaning |
|---|---|---|
| `Authorization` | every `/bindery/v1` request | `Bearer <shared token>` |
| `X-Bindery-Job-Id` | `download`, artifact, cancel | The job this request belongs to |
| `X-Bindery-Base` | UI requests | Public path prefix the plugin's UI is mounted at (§6) |
| `X-Bindery-Csrf` | UI requests | Opaque; a plugin **MUST NOT** need to interpret it |
| `User-Agent` | every request | `Bindery/<version>` |

---

## 3. Endpoints

| Method | Path | Required |
|---|---|---|
| `GET` | `/healthz` | yes |
| `GET` | `/bindery/v1/manifest` | yes |
| `POST` | `/bindery/v1/probe` | no — see `capabilities.probe` |
| `POST` | `/bindery/v1/download` | yes |
| `GET` | `/bindery/v1/artifacts/{jobId}/{artifactId}` | yes |
| `DELETE` | `/bindery/v1/jobs/{jobId}` | yes |
| `POST` | `/bindery/v1/actions/{action}` | no — see `actions` (§5) |
| `GET`/`POST` | `/bindery/v1/ui/{*path}` | no — only for `ui.mode` `fragment` or `iframe` (§6) |

### 3.1 `GET /healthz`

Liveness. **MUST** return `200` with a JSON body when the process is able to serve
requests. The body **SHOULD** be `{"status":"ok"}`; Bindery only inspects the status code.

This endpoint **MUST** be cheap and **MUST NOT** perform network I/O to third-party sites.

### 3.2 `GET /bindery/v1/manifest`

Self-description. **MUST** return `200 application/json`. The response **SHOULD** be stable
for the lifetime of the process; Bindery caches it and refreshes on a configurable interval.

```jsonc
{
  "protocolVersion": 1,
  "name": "fanficfare",
  "displayName": "FanFicFare",
  "version": "0.1.0",
  "homepage": "https://github.com/JimmXinu/FanFicFare",
  "description": "Downloads fanfiction from AO3, FFN, and ~50 other sites.",
  "priority": 100,
  "matches": [
    "^https?://(www\\.)?archiveofourown\\.org/works/\\d+",
    "^https?://(www\\.)?fanfiction\\.net/s/\\d+"
  ],
  "formats": ["epub"],
  "capabilities": {
    "probe": true,
    "update": true,
    "metadata": true,
    "cover": true,
    "cancel": true
  },
  "config": [ /* section 4 */ ],
  "actions": [ /* section 5 */ ],
  "ui": { "mode": "declarative" }
}
```

| Field | Type | Required | Notes |
|---|---|---|---|
| `protocolVersion` | int | yes | `1` |
| `name` | string | yes | Stable identity. **MUST** match `^[a-z0-9][a-z0-9-]{0,31}$`. Used in URLs, config keys, and the DB. Changing it is installing a different plugin. |
| `displayName` | string | no | Defaults to `name`. |
| `version` | string | yes | The plugin's own version. Free-form; SemVer **SHOULD** be used. |
| `homepage` | string | no | Absolute `https:` URL. |
| `description` | string | no | One or two sentences, plain text. |
| `priority` | int | no | Default `0`. Higher wins when several plugins claim a URL. |
| `matches` | string[] | no | Regexes matched against the whole URL, case-insensitively. Empty means "claims nothing by pattern" — such a plugin is only reachable via `probe` or explicit selection. |
| `formats` | string[] | no | Lowercase extensions the plugin can produce, best first. Default `["epub"]`. |
| `capabilities` | object | no | Missing members are `false`, except `cancel`, which defaults to `true`. |
| `config` | array | no | Field schema, §4. |
| `actions` | array | no | Declarative actions, §5. |
| `ui` | object | no | Defaults to `{"mode":"declarative"}`. §6. |

**Regex safety.** `matches` entries are compiled by the host with a timeout and a length
cap. A plugin **SHOULD** keep them anchored and simple. A pattern the host cannot compile is
dropped with a logged warning; it does not disable the plugin.

### 3.3 `POST /bindery/v1/probe`

Optional. Declared by `capabilities.probe`. Lets a plugin answer for URLs its regexes cannot
express — and lets it decline URLs they wrongly match.

Request:

```json
{ "url": "https://archiveofourown.org/works/12345" }
```

Response `200`:

```json
{ "supported": true, "confidence": 0.95, "reason": "AO3 work id 12345" }
```

- `supported` — boolean, required.
- `confidence` — number in `[0,1]`, optional, default `1.0` when supported and `0.0`
  otherwise. Used as a tiebreak *after* `priority`.
- `reason` — short plain-text string, optional. Shown in host logs and diagnostics only.

Probe **MUST** be fast (host timeout is 5 seconds by default) and **SHOULD NOT** hit the
network. Probe failing — timeout, 5xx, malformed body — **MUST NOT** be fatal: the host
falls back to `matches`.

### 3.4 `POST /bindery/v1/download`

The main event.

Request:

```jsonc
{
  "jobId": "9f1b2c3d-0000-4444-8888-aaaabbbbcccc",
  "url": "https://archiveofourown.org/works/12345",
  "config": { "ao3_username": "...", "ao3_password": "...", "personal_ini": "..." },
  "options": {
    "update": false,
    "knownSourceId": null,
    "knownUpdated": null,
    "formats": ["epub"]
  }
}
```

| Field | Type | Required | Notes |
|---|---|---|---|
| `jobId` | string | yes | Host-generated UUID. Scopes artifacts and cancellation. Unique per attempt — a retry is a new `jobId`. |
| `url` | string | yes | The URL the user pasted, unmodified. |
| `config` | object | yes | Flat string map of the plugin's own `config` keys. Absent keys mean unset. Secrets arrive in cleartext over the plugin connection; they are encrypted at rest on the host. |
| `options.update` | bool | no | `true` means "this is a refresh of a book already in the library". A plugin without `capabilities.update` **MUST** treat it as a normal download. |
| `options.knownSourceId` | string\|null | no | The `sourceId` the host already holds for this book, when updating. |
| `options.knownUpdated` | string\|null | no | RFC 3339 timestamp of what the host already holds. A plugin **MAY** answer `unchanged` without downloading. |
| `options.formats` | string[] | no | Preference order. A plugin **SHOULD** honour it and **MAY** ignore it. |

Response: `200 application/x-ndjson`, streamed.

- Each line is one complete JSON object followed by `\n`. No line may contain a raw newline.
- The plugin **MUST** flush after every line. Buffering the stream defeats its purpose.
- A line **MUST NOT** exceed 64 KiB.
- The stream **MUST** end with exactly one `result` event, and the plugin **MUST** then
  close the response.
- Any non-`200` status is a transport-level failure and is retried by the host per its own
  policy. Once `200` has been sent, failures are reported as a `result` event with
  `status: "error"` — not by hanging up.

#### Events

**`progress`** — zero or more.

```json
{"event":"progress","percent":40,"message":"chapter 4/10"}
```

`percent` is a number in `[0,100]`, optional (omit when genuinely unknown), and **SHOULD**
be non-decreasing. `message` is optional plain text, one short line.

**`log`** — zero or more.

```json
{"event":"log","level":"info","message":"logged in to AO3"}
```

`level` is one of `debug`, `info`, `warn`, `error`. Log events are surfaced in the job
detail view and the host's log. A plugin **MUST NOT** log secrets — the host does not
redact for you.

**`result`** — exactly one, last.

```jsonc
{"event":"result","status":"ok",
 "artifacts":[
   {"id":"a1","filename":"Some Story.epub","format":"epub","contentType":"application/epub+zip",
    "kind":"book","primary":true,"bytes":481920,"sha256":"..."},
   {"id":"cover","filename":"cover.jpg","format":"jpg","contentType":"image/jpeg",
    "kind":"cover","primary":false,"bytes":40218}
 ],
 "metadata":{
   "title":"Some Story","authors":["Some Author"],
   "series":"Some Series","seriesIndex":1,
   "summary":"...","language":"en","tags":["Fluff","Slow Burn"],
   "sourceId":"ao3:12345","sourceUrl":"https://archiveofourown.org/works/12345",
   "publisher":"Archive of Our Own","chapters":10,
   "published":"2025-01-02T00:00:00Z","updated":"2026-08-01T00:00:00Z"
 }}
```

Terminal `status` values:

| `status` | Meaning | Additional fields |
|---|---|---|
| `ok` | Work produced. | `artifacts` (at least one, exactly one `primary`), `metadata` |
| `unchanged` | Nothing new since `options.knownUpdated`. | none; `artifacts` **MUST** be absent or empty |
| `error` | Failed. | `error` |

**Artifact object**

| Field | Type | Required | Notes |
|---|---|---|---|
| `id` | string | yes | Unique within the job. **MUST** match `^[A-Za-z0-9._-]{1,64}$` — it goes in a URL. |
| `filename` | string | yes | Suggested name. The host sanitizes it; a plugin **MUST NOT** include path separators. |
| `format` | string | yes | Lowercase extension, e.g. `epub`, `jpg`. |
| `contentType` | string | no | Defaults by `format`. |
| `kind` | string | no | `book` (default) or `cover`. |
| `primary` | bool | no | Exactly one artifact of a successful result **MUST** be `primary: true`. |
| `bytes` | int | no | Advisory. The host does not trust it. |
| `sha256` | string | no | Lowercase hex. If present the host **MUST** verify it and fail the job on mismatch. |

**Metadata object** — `title` is the only required member. All others are optional and a
missing member is not an error.

`authors` and `tags` are arrays of strings. `seriesIndex` is a number. `chapters` is an int.
`published` and `updated` are RFC 3339 timestamps. `language` is a BCP 47 tag.

`sourceId` is the plugin's stable identity for the work, and **SHOULD** be namespaced
(`ao3:12345`). It is what makes updates and duplicate detection work; a plugin that can
produce one **SHOULD**.

**Error object**

```json
{"event":"result","status":"error",
 "error":{"code":"auth_required","message":"AO3 rejected the credentials","retryable":false}}
```

| `code` | Retryable by default | Meaning |
|---|---|---|
| `unsupported_url` | no | The plugin does not handle this URL after all. |
| `not_found` | no | The work does not exist, or was deleted. |
| `auth_required` | no | Site credentials missing, wrong, or insufficient. |
| `rate_limited` | yes | The site is throttling. Host backs off. |
| `network` | yes | Transient network or upstream 5xx. |
| `parse` | no | The site's HTML changed; the plugin could not read it. |
| `cancelled` | no | The job was cancelled. |
| `internal` | yes | Anything else. |

`retryable` is authoritative — the table is the default when the plugin omits it. `message`
is plain text for humans and **MUST NOT** contain secrets.

#### Cancellation by disconnect

If the host disconnects the `download` response, the plugin **MUST** treat the job as
cancelled: stop work, release resources, and delete artifacts. It **MUST NOT** keep working
in the hope the host comes back.

### 3.5 `GET /bindery/v1/artifacts/{jobId}/{artifactId}`

Returns the bytes. **MUST** set `Content-Type` and **SHOULD** set `Content-Length`.
**SHOULD** support `HEAD`. Range requests are not required.

Artifacts **MUST** remain retrievable for at least **15 minutes** after the `result` event,
or until the job is deleted, whichever is first. `404` for an unknown job or artifact.

**Files move over HTTP.** There is no shared volume between host and plugin, by design.

### 3.6 `DELETE /bindery/v1/jobs/{jobId}`

Cancels the job if running and deletes its artifacts. **MUST** return `204` and **MUST** be
idempotent — deleting an unknown or already-deleted job is `204`, not `404`.

The host calls this after it has fetched every artifact it wants. A plugin **MUST** also
expire artifacts on its own so that a host crash does not leak disk forever.

---

## 4. Config schema

`config` is an array of field descriptors. Bindery renders the form, validates input, stores
values (secrets encrypted at rest), and passes them back on every `download` and action
call. A `declarative` plugin ships no HTML for this.

```jsonc
{ "key": "ao3_username", "label": "AO3 username", "type": "string",
  "required": false, "default": "", "help": "Only needed for restricted works.",
  "placeholder": "username" }
```

| Field | Required | Notes |
|---|---|---|
| `key` | yes | `^[a-z0-9_]{1,64}$`. Unique within the plugin. |
| `label` | yes | Short human label. |
| `type` | yes | See table below. |
| `required` | no | Default `false`. |
| `default` | no | Prefilled value. **MUST NOT** be set for `secret`. |
| `help` | no | One sentence, plain text, rendered under the field. |
| `placeholder` | no | Input placeholder. |
| `options` | for `select` | Array of `{ "value": "...", "label": "..." }`. |
| `min` / `max` | no | Numeric bounds for `int`; length bounds for `string`/`text`. |
| `pattern` | no | Regex the value must match. Validated host-side. |

| `type` | Rendered as | Sent as |
|---|---|---|
| `string` | single-line text | string |
| `secret` | password input, write-only | string |
| `text` | textarea (monospace) | string |
| `bool` | checkbox | `"true"` / `"false"` |
| `int` | number input | decimal string |
| `select` | dropdown from `options` | string |
| `url` | text input, URL-validated | string |

Values are always transmitted as strings — the map is flat and untyped on the wire, which
keeps plugin-side parsing trivial in every language.

**Secrets.** A `secret` value is stored encrypted at rest, never logged, and never rendered
back into HTML in cleartext — the UI shows "set" or "not set" and offers replacement. It is
sent to the plugin in cleartext because the plugin needs it to log in; that connection is
in-pod (`sidecar`) or in-cluster (`service`).

---

## 5. Actions

Actions are Tier 1's escape from "settings only": a plugin declares a named operation with
typed input and output, and Bindery renders a generic UI for it. This is how a plugin gets
"search AO3 and pick a result" without shipping a byte of HTML.

Manifest:

```jsonc
"actions": [
  { "name": "search", "label": "Search AO3", "description": "Find a work by title or author.",
    "input": [ { "key": "q", "label": "Query", "type": "string", "required": true } ],
    "output": { "kind": "list", "itemAction": "download" } }
]
```

- `name` — `^[a-z0-9_]{1,32}$`, unique within the plugin.
- `input` — the same field descriptors as §4.
- `output.kind` — `list`, `text`, or `message`.
- `output.itemAction` — for `list` only. `download` renders each item's `url` with a
  "Download" button; `none` renders items inertly. Default `none`.

Invocation — `POST /bindery/v1/actions/{name}`:

```json
{ "action": "search", "input": { "q": "hobbit" }, "config": { "key": "value" } }
```

Response `200`:

```jsonc
{ "status": "ok",
  "output": { "kind": "list",
    "items": [ { "title": "The Hobbit, Rewritten", "url": "https://example/works/1",
                 "subtitle": "by someone - 12 chapters", "thumbnail": "https://example/c.jpg" } ] } }
```

or `{ "status": "error", "error": { "code": "...", "message": "...", "retryable": false } }`.

`text` output carries `{"kind":"text","text":"..."}` and is rendered in a `<pre>`; `message`
carries `{"kind":"message","level":"info","message":"..."}`.

Actions are synchronous and **MUST** complete within the host timeout (30 seconds default).
Anything longer belongs in `download`.

---

## 6. UI endpoints

Only for `ui.mode` of `fragment` or `iframe`. The full contract, including the sanitizer
allowlist and the reasoning behind the no-JavaScript rule, is
[`PLUGIN-UI.md`](PLUGIN-UI.md). In protocol terms:

- The plugin serves `GET`/`POST` under `/bindery/v1/ui/`.
- Bindery reverse-proxies `/plugins/{name}/ui/{**rest}` to `/bindery/v1/ui/{**rest}`,
  forwarding method, query string, and body.
- Responses **MUST** be `text/html` (or `text/event-stream` for SSE). Any other content type
  is rejected by the proxy, not forwarded.
- A `fragment` response **MUST** be a fragment: no `<html>`, `<head>`, `<body>`, or
  `<!doctype>`.
- A `fragment` response **MUST NOT** contain JavaScript in any form. The sanitizer strips
  it; shipping it is a conformance failure, not a style issue.
- Links and `hx-*` targets **MUST** be built from the `X-Bindery-Base` header. Bindery does
  not rewrite attributes.

---

## 7. Error model

Every non-streaming endpoint returns errors as:

```json
{ "error": { "code": "auth_required", "message": "missing bearer token", "retryable": false } }
```

with an appropriate status: `400` malformed request, `401` bad token, `404` unknown job or
artifact, `422` semantically invalid input, `429` plugin-side rate limit, `500` internal,
`503` not ready.

`download` is the exception: once it has answered `200`, errors travel as a `result` event.

---

## 8. Configuration a plugin receives

Environment, not files. A plugin **MUST** read:

| Variable | Meaning |
|---|---|
| `BINDERY_PLUGIN_TOKEN` | Shared bearer token. Unset means no auth (dev only). |
| `BINDERY_PLUGIN_PORT` | Port to bind. Default `8080`. |
| `BINDERY_PLUGIN_WORKDIR` | Writable scratch directory for artifacts. Default a temp dir. |

A plugin **MAY** read its own variables, but everything user-configurable **SHOULD** be in
the manifest's `config` so Bindery can render it. Requiring an operator to set an env var
that the UI does not know about is a design smell.

---

## 9. Limits

| Limit | Value | Enforced by |
|---|---|---|
| Manifest body | 256 KiB | host |
| NDJSON line | 64 KiB | both |
| Fragment response | 512 KiB | host |
| Artifact size | 512 MiB (configurable) | host |
| Probe timeout | 5 s | host |
| Action timeout | 30 s | host |
| Manifest/health timeout | 10 s | host |
| Download idle timeout | 300 s between events | host |
| Artifact retention | at least 15 min after result | plugin |

A host **MUST** enforce its limits by aborting, not by trusting the plugin's declared sizes.

---

## 10. Conformance

`tests/conformance/` is a black-box suite. It needs a plugin's base URL and nothing else —
Bindery is not involved:

```bash
python tests/conformance/run.py --base-url http://localhost:8080 --token dev
```

It checks: auth enforcement, manifest schema and constraints, probe behaviour, the NDJSON
stream's shape and ordering, artifact retrieval and content types, cancellation idempotency,
action schemas, and — for `fragment` plugins — that fragments are fragments, honour
`X-Bindery-Base`, and carry no JavaScript. XSS vectors from `tests/conformance/vectors/`
are the shared corpus the host's sanitizer tests use too.

Passing the suite is what "is a plugin" means. Nothing else is required — not a language,
not a framework, not a base image.

---

## 11. Changelog

| Version | Change |
|---|---|
| 1 | Initial contract. |
