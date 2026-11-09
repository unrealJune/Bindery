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
| `X-Bindery-Notify` | every `/bindery/v1` request | Absolute URL for change notifications (§3.7). Absent when the host accepts none |
| `X-Bindery-Notify-Token` | every `/bindery/v1` request | Bearer token for that URL. **MUST NOT** be logged |
| `X-Bindery-User` | UI, action, `download`, circulation — when a person is acting (§2.3) | Opaque, stable pseudonym for that person |
| `X-Bindery-Role` | whenever `X-Bindery-User` is sent | `admin` or `member` (§2.3) |
| `User-Agent` | every request | `Bindery/<version>` |

### 2.3 People

One Bindery serves several people. Each has their own shelf, their own e-readers, and their
own circulation accounts; some of them are **admins**, who administer the installation —
its plugins, its shared configuration, and its library volume. Which people are admins is
the host's business (it comes from the identity provider) and is never the plugin's.

A request made on a person's behalf carries who they are:

- `X-Bindery-User` is an opaque pseudonym for the person. It is **stable** — the same
  person always produces the same value for the same plugin, across host restarts — and it
  is **per plugin**, so two plugins cannot correlate their users with each other. A plugin
  **MAY** key per-person state on it and **MUST NOT** try to interpret it. It is not an
  email, a name, or the identity provider's subject.
- `X-Bindery-Role` is `admin` or `member`. A plugin **MAY** rely on it to refuse an
  operation to a member, and **SHOULD** do so for any screen or endpoint that changes what
  every person sees. The host also enforces the declarations in §5 and §6, so the header is
  defence in depth rather than the only gate.

Both headers are **absent** when no person is acting — a background job nobody asked for.
A plugin **MUST** treat that as "no person", not as an admin. Neither header is ever taken
from the browser: the host sets them itself and drops any a client sends.

Which requests act for a person, and with what role:

| Request | Acts for | `X-Bindery-Role` |
|---|---|---|
| UI (§6) | the signed-in person viewing the plugin | theirs |
| Action (§5) | the person who invoked it | theirs |
| `download` | the person who queued it; for an unattended update, the earliest person holding the book on their shelf, whose user-scoped `config` (§4) is also the one sent | always `member` |
| Circulation (§3.8) | the owner of the account the call carries | always `member` |

A download or circulation call is never an administrative operation, and most run long
after anyone was present to hold a role — so the host sends `member` for them regardless of
who the person is. A plugin that wants to know whether someone is an admin learns it from a
UI or action request, never from these.

---

## 3. Endpoints

Everything in this table is served by the **plugin** and called by Bindery. The single
exception is §3.7, which Bindery serves and the plugin calls.

| Method | Path | Required |
|---|---|---|
| `GET` | `/healthz` | yes |
| `GET` | `/bindery/v1/manifest` | yes |
| `POST` | `/bindery/v1/probe` | no — see `capabilities.probe` |
| `POST` | `/bindery/v1/download` | yes |
| `GET` | `/bindery/v1/artifacts/{jobId}/{artifactId}` | yes |
| `DELETE` | `/bindery/v1/jobs/{jobId}` | yes |
| `POST` | `/bindery/v1/actions/{action}` | no — see `actions` (§5) |
| `GET`/`POST` | `/bindery/v1/ui/{*path}` | no — only for `ui.mode` `sandboxed` or `fragment` (§6) |
| `POST` | `{notifyUrl}` — *served by Bindery* | no — see `capabilities.notify` (§3.7) |
| `POST` | `/bindery/v1/circulation/verify` | no — see `capabilities.circulation` (§3.8) |
| `POST` | `/bindery/v1/circulation/search` | no — §3.8 |
| `POST` | `/bindery/v1/circulation/snapshot` | no — §3.8 |
| `POST` | `/bindery/v1/circulation/holds` | no — §3.8 |
| `POST` | `/bindery/v1/circulation/holds/{holdId}/{operation}` | no — §3.8 |

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
    "cancel": true,
    "notify": false,
    "circulation": false
  },
  "config": [ /* section 4 */ ],
  "actions": [ /* section 5 */ ],
  "circulation": { /* section 3.8, only with capabilities.circulation */ },
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
| `circulation` | object | with `capabilities.circulation` | Account schema for library circulation, §3.8. |
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
| `config` | object | yes | Flat string map of the plugin's own `config` keys: install-scoped values overlaid with the acting person's user-scoped ones (§4.1). Absent keys mean unset. Secrets arrive in cleartext over the plugin connection; they are encrypted at rest on the host. |
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

### 3.7 `POST {notifyUrl}` — change notification

The one endpoint in this protocol that **Bindery** serves and a **plugin** calls. Every
other exchange is host-pull; this inverts it, so its scope is deliberately tiny.

A plugin that learns about new content out-of-band — a chat bot receiving a file, a webhook,
anything not driven by a Bindery request — has no way to say so. It can only wait to be
polled. This endpoint lets it raise a hand. It carries **no content**: it names a source and
says "worth re-fetching". Bindery decides whether, and when, to act.

**Discovery.** Bindery advertises the endpoint on every `/bindery/v1` request via the
headers in §2.2:

| Header | Meaning |
|---|---|
| `X-Bindery-Notify` | Absolute URL to POST to |
| `X-Bindery-Notify-Token` | Bearer token to authenticate with |

Both are absent when the host does not accept notifications. A plugin **MAY** persist the
most recent pair so it can notify outside a request (this is the whole point — a bot is not
serving a request when a file arrives), and **MUST** tolerate their absence by doing nothing
extra. A plugin **MUST NOT** log the token.

**Request.**

```jsonc
POST /bindery/v1/notify
Authorization: Bearer <X-Bindery-Notify-Token>
Content-Type: application/json

{
  "plugin": "hedgerow",
  "sourceUrl": "https://www.royalroad.com/fiction/140087",
  "reason": "new-chapters"
}
```

| Field | Type | Required | Notes |
|---|---|---|---|
| `plugin` | string | yes | The notifying plugin's `name`. **MUST** match the token's plugin. |
| `sourceUrl` | string | yes | Identifies the affected book, compared against `Book.sourceUrl`. **SHOULD** be the same normalized form the plugin returns in download metadata. |
| `reason` | string | no | Free-form, for logs. Not interpreted. |

**Responses.** `202` accepted (an update was queued). `200` with `{"accepted": false,
"reason": "..."}` when understood but not acted on — unknown source, a job already running,
or debounced; this is **not** an error. `401` bad token. `429` rate-limited.

**It is a hint, never a command.** Bindery **MAY** coalesce, defer, rate-limit, or ignore
any notification, and a plugin **MUST NOT** depend on one being honoured. The host's own
update schedule is the backstop that makes this endpoint an optimization rather than a
correctness requirement — if every notification were dropped, books would still converge on
the next scheduled pass, just later.

**Host obligations.** Bindery **MUST** issue a distinct token per plugin, compare it in
constant time, and resolve the caller's identity from the *token* — never from the `plugin`
field, which is a claim to be checked against it, not trusted. It **MUST** only act on books
whose `sourcePlugin` is that plugin, so a compromised plugin cannot drive refreshes of books
it does not own. It **MUST** rate-limit per plugin and debounce per book.

**Plugin obligations.** A plugin **SHOULD** debounce — one notification after a burst of
arrivals, not one per file — and **MUST** treat any failure as routine: no retry storm, no
blocking the work that triggered it. Declared by `capabilities.notify`.

### 3.8 Circulation — library search, holds, and loans

Optional. Declared by `capabilities.circulation`. Everything else in this protocol produces
a *file*. Circulation produces *state*: a hold on a physical book sits in a queue for weeks,
moves to a branch, waits on a shelf with a pickup deadline, becomes a loan, and comes due.
None of that is an artifact, so it does not travel through `download`.

The division of labour is the same one the rest of the protocol uses — the plugin knows the
site, the host owns everything durable:

- **The plugin** speaks to the library's catalog: it searches, places and changes holds, and
  reports what an account currently holds and has checked out.
- **The host** owns the accounts (and their secrets), the lifecycle of every hold and loan,
  the history of transitions, and any notification sent about them. A plugin never sends a
  message to a person and never stores a person's credentials.

A circulation plugin is still a plugin: it **MUST** serve every required endpoint in §3. One
that downloads nothing **SHOULD** declare no `matches` and **MUST** answer `download` with a
well-formed `result` of `status: "error"`, `code: "unsupported_url"`.

#### Accounts

Library credentials belong to a *person*, not to the installation — two people in one
household have two cards, and one person may have cards at two library systems. A
user-scoped `config` field (§4.1) gives each person one value; circulation needs *any
number*, so it is driven by **accounts**: the host lets each signed-in person connect any
number of accounts to a circulation plugin, using a field schema the plugin declares.
Every circulation call carries the account owner's `X-Bindery-User` (§2.3).

```jsonc
"circulation": {
  "accountFields": [
    { "key": "library", "label": "Library", "type": "select", "required": true,
      "options": [ { "value": "seattle", "label": "The Seattle Public Library" },
                   { "value": "kcls",    "label": "King County Library System" } ] },
    { "key": "card", "label": "Library card number", "type": "string", "required": true },
    { "key": "pin",  "label": "PIN or password",     "type": "secret", "required": true }
  ],
  "mediums": ["physical", "digital"],
  "holdOperations": ["cancel", "suspend", "resume"]
}
```

| Field | Required | Notes |
|---|---|---|
| `accountFields` | yes | Field descriptors exactly as §4, validated and stored by the host the same way — `secret` values encrypted at rest and never rendered back. |
| `mediums` | no | Which of `physical`, `digital` the plugin can return. Default both. |
| `holdOperations` | no | Which hold operations the plugin implements. Default `["cancel"]`. |

Every circulation request carries the account it acts for:

```json
"account": { "id": "1f0c…", "fields": { "library": "seattle", "card": "…", "pin": "…" } }
```

- `id` is host-generated, opaque, and stable for the life of the account. A plugin **MAY**
  key a cache of remote sessions on it, and **MUST NOT** interpret it.
- `fields` arrive in cleartext for the same reason `config` secrets do (§4). A plugin
  **MUST NOT** persist them, **MUST NOT** log them, and **MUST** discard any session it
  cached for an `id` whose `fields` have changed.

#### Mediums

Every catalog item, hold, and loan carries a `medium`, and a host **MUST** show it, because
the two behave nothing alike and conflating them misleads the person relying on it:

| `medium` | Means | A ready hold means |
|---|---|---|
| `physical` | A copy on a shelf: book, large print, audiobook on CD, DVD. | Go to a branch before `readyUntil`. |
| `digital` | A licence through a lending platform (OverDrive/Libby, hoopla, …). | Borrow it in the platform's app before `readyUntil`. |

`format` refines it — `book`, `large_print`, `graphic_novel`, `ebook`, `audiobook`,
`audiobook_cd`, `dvd`, `bluray`, `music_cd`, `magazine`, `other` — and `formatLabel` is the
library's own wording, for display.

#### `POST /bindery/v1/circulation/verify`

Checks an account's credentials when a person connects it, and returns what the host needs
to describe it.

```jsonc
// request
{ "account": { "id": "…", "fields": { … } } }
// response 200
{ "status": "ok",
  "library": { "id": "seattle", "name": "The Seattle Public Library" },
  "displayName": "card ending 4821",
  "pickupLocations": [ { "id": "3", "name": "Central Library" } ] }
```

`displayName` **MUST NOT** contain the full card number or any secret. `pickupLocations`
**MAY** be empty, in which case a hold is placed at the account's default branch.

#### `POST /bindery/v1/circulation/search`

```jsonc
// request
{ "account": { … }, "query": "dune", "medium": "physical", "page": 1 }
// response 200
{ "status": "ok", "total": 228, "page": 1, "pages": 10,
  "items": [ {
    "id": "S82C1234567",
    "title": "Dune", "subtitle": null, "authors": ["Herbert, Frank"], "year": "2019",
    "url": "https://kcls.bibliocommons.com/v2/record/S82C1234567",
    "coverUrl": "https://…",
    "medium": "physical", "format": "book", "formatLabel": "Book",
    "provider": null,
    "availability": { "status": "unavailable", "copies": 12, "availableCopies": 0,
                      "holds": 24, "onOrder": 0 },
    "holdable": true,
    "borrowUrl": null
  } ] }
```

- `medium` in the request is `any` (default), `physical`, or `digital`. A plugin **MUST**
  honour it: every returned item's `medium` matches the filter unless the filter is `any`.
- Search **MUST NOT** require the account's credentials to be valid. It uses the account to
  know *which* library to search; a person whose PIN has expired can still look things up.
- `availability.status` is one of `available`, `unavailable`, `on_order`, `not_holdable`,
  `library_use_only`, `unknown`. The counts are optional integers.
- `holdable` says whether `POST …/holds` will accept this item. A digital item the plugin
  cannot place a hold on **SHOULD** carry `borrowUrl`, an absolute `https:` URL where the
  person can borrow it themselves.
- `provider` names the lending platform for digital items, e.g. `"OverDrive"`.

#### `POST /bindery/v1/circulation/snapshot`

Everything the account currently has: every active hold and every current loan.

```jsonc
// request
{ "account": { … } }
// response 200
{ "status": "ok",
  "holds": [ {
    "id": "h-998877", "itemId": "S82C1234567",
    "title": "Dune", "authors": ["Herbert, Frank"], "coverUrl": "…", "url": "…",
    "medium": "physical", "format": "book",
    "state": "ready",
    "queuePosition": null, "queueSize": null,
    "pickupLocation": { "id": "3", "name": "Central Library" },
    "readyUntil": "2026-11-14T00:00:00Z",
    "suspendedUntil": null,
    "placedAt": "2026-10-01T17:02:00Z"
  } ],
  "loans": [ {
    "id": "c-112233", "itemId": "S82C7654321",
    "title": "…", "authors": [], "medium": "physical", "format": "book",
    "dueAt": "2026-11-27T00:00:00Z", "overdue": false, "renewable": true
  } ] }
```

Hold `state` is one of:

| `state` | Meaning |
|---|---|
| `pending` | Waiting in the queue. `queuePosition` / `queueSize` when known. |
| `in_transit` | A copy is assigned and moving to the pickup branch. |
| `ready` | Waiting for the person. `readyUntil` is the pickup (or borrow) deadline. |
| `suspended` | Paused by the person; keeps its place but is skipped. `suspendedUntil` when known. |

**A snapshot is complete or it is an error.** The host infers what *happened* — a hold
collected, a hold that lapsed on the shelf, a book returned — from what has *disappeared*
since the previous snapshot. A plugin that cannot see the whole account (a page failed to
load, a session expired mid-way) **MUST** answer with `status: "error"` rather than return a
partial list, because a partial list is indistinguishable from "every missing hold was
cancelled". The host, for its part, **MUST NOT** draw any conclusion from a failed snapshot.

Ids are the library's, stable for the life of the hold or loan, and namespaced within the
account. `itemId` is the catalog id that `search` returns, and is how the host recognizes
that a hold which vanished became the loan that appeared.

#### `POST /bindery/v1/circulation/holds`

Places a hold.

```jsonc
// request
{ "account": { … }, "itemId": "S82C1234567", "pickupLocationId": "3" }
// response 200
{ "status": "ok", "hold": { /* a hold object, as in snapshot */ } }
```

`pickupLocationId` is optional; without it the library's default for the account applies.
`hold` is optional in the response — the host's next snapshot will find it either way.

#### `POST /bindery/v1/circulation/holds/{holdId}/{operation}`

`operation` is `cancel`, `suspend`, or `resume`, and **MUST** be one the manifest lists in
`holdOperations`; anything else is `404`.

```jsonc
// request
{ "account": { … }, "until": "2026-12-20" }   // `until` only for suspend; optional
// response 200
{ "status": "ok", "hold": { … } }              // `hold` absent after cancel
```

#### Errors and timing

All five endpoints answer errors as the action envelope does (§5):
`{ "status": "error", "error": { "code": …, "message": …, "retryable": … } }` with `200`, or
the §7 envelope with a 4xx/5xx status for a malformed request. Additional codes for
circulation:

| `code` | Retryable by default | Meaning |
|---|---|---|
| `not_holdable` | no | The item cannot be held (reference only, digital without integration, …). |
| `already_held` | no | The account already has a hold on this item. |
| `hold_limit` | no | The account has reached the library's hold limit. |
| `account_blocked` | no | The library has blocked the account — fines, an expired card. |

`auth_required` means the account's credentials were refused, and is how the host learns to
ask the person to re-enter them. Every circulation call is synchronous and **MUST** finish
within the action timeout (§9).

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
| `scope` | no | `install` (default) or `user`. §4.1. |

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

### 4.1 Scope

Some settings describe the installation — a user agent, a rate limit, a shared source list.
Others belong to a person — their own login on the site the plugin downloads from. `scope`
says which:

- **`install`** (the default) — one value for the whole installation. Only an admin (§2.3)
  can change it, and every person's requests carry it.
- **`user`** — each person sets their own value, and only their own. The host stores it per
  person, with the same at-rest encryption for `secret`, and never shows one person's value
  to another — not even to an admin.

The `config` object a plugin receives stays one flat map: every install-scoped value, then
the acting person's (§2.3) user-scoped values over the top. A user-scoped key the person
has not set is absent, never filled in from someone else. When no person is acting, the
request carries install-scoped values only. An unrecognized `scope` is treated as `user`,
so a typo never shares one person's value with everyone.

`scope` applies to `config` alone. Action `input` (§5) is per call and circulation
`accountFields` (§3.8) are per account, so both are already a person's own; a host
**MUST** ignore `scope` on them.

---

## 5. Actions

Actions are Tier 1's escape from "settings only": a plugin declares a named operation with
typed input and output, and Bindery renders a generic UI for it. This is how a plugin gets
"search AO3 and pick a result" without shipping a byte of HTML.

Manifest:

```jsonc
"actions": [
  { "name": "search", "label": "Search AO3", "description": "Find a work by title or author.",
    "search": true,
    "input": [ { "key": "q", "label": "Query", "type": "string", "required": true } ],
    "output": { "kind": "list", "itemAction": "download" } }
]
```

- `name` — `^[a-z0-9_]{1,32}$`, unique within the plugin.
- `input` — the same field descriptors as §4.
- `output.kind` — `list`, `text`, or `message`.
- `output.itemAction` — for `list` only. `download` renders each item's `url` with a
  "Download" button; `none` renders items inertly. Default `none`.
- `access` — `member` (default) or `admin`. An `admin` action changes something every
  person sees; the host **MUST** refuse to invoke it for anyone but an admin (§2.3) and
  **SHOULD NOT** offer it to them. An unrecognized value is treated as `admin` — a typo
  fails closed.
- `search` — boolean, default `false`. Opts this action into the host's unified search
  alongside the person's shelf and library catalogs. Such an action **MUST** be read-only,
  accept exactly one required `string` input named `q`, and return
  `{"kind":"list","itemAction":"download"}`. The host may invoke it while rendering a
  search page, but **MUST NOT** queue a download until the person chooses a result.
  Access rules and per-person config still apply. The host **MUST NOT** infer this flag
  from an action's name, or invoke other actions automatically. Plugins that only accept
  URLs need not offer keyword search; their existing intake flow is unchanged.

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

Only for `ui.mode` of `sandboxed` or `fragment`. The full contract — the bridge message
types, the frame's CSP, and the reasoning behind both — is [`PLUGIN-UI.md`](PLUGIN-UI.md).
In protocol terms:

- The plugin serves `GET`/`POST` under `/bindery/v1/ui/`.
- Bindery reverse-proxies `/plugins/{name}/ui/{**rest}` to `/bindery/v1/ui/{**rest}`,
  forwarding method, query string, and body.
- `X-Bindery-Base` carries the public path prefix the UI is mounted at. Bindery **does not**
  rewrite attributes in a response, in either mode.
- `X-Bindery-User` and `X-Bindery-Role` identify the person viewing it (§2.3). A UI
  request always has one.
- `ui.adminPaths` is an optional array of path prefixes — `"/sources"` — reserved for
  admins. The host **MUST** answer a member's request for one with `403` without
  forwarding it. A prefix matches the path itself and anything below it (`/sources` and
  `/sources/3`, not `/sourcesx`), compared after the host has normalized the path.
- A `ui.nav` entry **MAY** carry `access`: `member` (default) or `admin` (an unrecognized
  value is `admin`). An `admin` entry is not linked for members. This is visibility only — a
  screen is protected by listing it in `adminPaths`, and a plugin **SHOULD** also check
  `X-Bindery-Role` itself.

### 6.1 `mode: "sandboxed"` — the recommended tier

The plugin serves a **complete document** and Bindery loads it into an iframe with an opaque
origin (`sandbox` without `allow-same-origin`).

- A response **MUST** be a full document: `<!doctype html>`, `<head>`, `<body>`.
- A plugin **MAY** ship JavaScript and CSS without restriction. It has no access to
  Bindery's origin, cookies, DOM, or API, and the frame's CSP sets `connect-src 'none'`.
- Responses **MUST** carry a content type within the proxy's allowlist: `text/html`,
  `text/css`, `text/javascript`, `application/javascript`, `application/json`, `image/*`,
  `font/*`, `text/event-stream`. Subresources under the plugin's own mount point are
  forwarded, so a plugin **MAY** serve its own `app.js` and `style.css`.
- All communication with the host goes through the `postMessage` bridge. A plugin **MUST
  NOT** rely on `fetch` or `XMLHttpRequest`; CSP blocks them.
- Relative URLs resolve correctly, because the document is loaded at its mount path.

### 6.2 `mode: "fragment"` — deprecated

Still supported and still enforced exactly as before. New plugins **SHOULD** use
`sandboxed` or `declarative` instead.

- A response **MUST** be a fragment: no `<html>`, `<head>`, `<body>`, or `<!doctype>`.
- A response **MUST** be `text/html` (or `text/event-stream` for SSE). Any other content
  type is rejected by the proxy, not forwarded.
- A response **MUST NOT** contain JavaScript in any form. The host sanitizes it; shipping it
  is a conformance failure, not a style issue.
- Links and `hx-*` targets **MUST** be built from the `X-Bindery-Base` header.

`iframe` is accepted as a deprecated alias for `sandboxed`.

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
| Sandboxed UI response | 4 MiB (configurable) | host |
| Artifact size | 512 MiB (configurable) | host |
| Probe timeout | 5 s | host |
| Action timeout | 30 s | host |
| Circulation call timeout | 30 s (the action timeout) | host |
| Circulation response body | 2 MiB | host |
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
action schemas, circulation (§3.8: manifest, request validation, and — given `--account` —
search, the medium filter, verify, and snapshot; placing a real hold is opt-in), and — for
`fragment` plugins — that fragments are fragments, honour
`X-Bindery-Base`, and carry no JavaScript. XSS vectors from `tests/conformance/vectors/`
are the shared corpus the host's sanitizer tests use too.

Passing the suite is what "is a plugin" means. Nothing else is required — not a language,
not a framework, not a base image.

---

## 11. Changelog

| Version | Change |
|---|---|
| 1 | Initial contract. |
| 1 (additive) | Actions may opt into unified keyword search with `search: true` (§5). Absent means false; older hosts still render these as ordinary actions. No new endpoint or protocol version is needed. |
| 1 (additive) | `capabilities.notify` and §3.7 added — a plugin that learns of new content out-of-band may POST a contentless hint to Bindery, which decides whether to queue an update. No version bump: the capability defaults to `false`, the advertising headers are optional, and a host that serves no endpoint simply omits them. The host's update schedule remains the backstop, so the feature is an optimization and never a correctness requirement. |
| 1 (additive) | `capabilities.circulation`, the manifest's `circulation` block, and §3.8 added — library search, holds, and loans, with per-person accounts and an explicit `physical`/`digital` medium on every item. New error codes `not_holdable`, `already_held`, `hold_limit`, `account_blocked`; older hosts already treat unknown codes as `internal`. No version bump: the capability defaults to `false`, a host that does not implement it never calls the new endpoints, and a circulation plugin still serves every required endpoint. |
| 1 (additive) | `ui.mode: "sandboxed"` added — a full document in an opaque-origin iframe, plugin JavaScript permitted, host communication via the `postMessage` bridge. `fragment` deprecated but unchanged. `iframe` becomes a deprecated alias for `sandboxed`. No version bump: unknown `ui.mode` values already degrade to `declarative`. |
| 1 (additive) | Several people per installation (§2.3). New headers `X-Bindery-User` and `X-Bindery-Role`; config `scope` (§4.1); `access` on actions and `ui.nav` entries; `ui.adminPaths`. No version bump: every new member is optional with a default that reproduces the old behaviour — `scope` defaults to `install`, `access` to `member` — and an older plugin that ignores the headers still works, with all of its configuration shared as before. |
