# Plugin UIs

Status: **normative for protocol v1.**

A plugin may contribute a real interface to Bindery. This document is the contract for how,
and — more importantly — the constraints that make it safe.

There are two tiers worth building:

| mode | Plugin ships | JavaScript | Isolation |
|---|---|---|---|
| `declarative` | nothing — Bindery renders forms from the config schema | n/a | total |
| `sandboxed` | a complete document, its own JS and CSS | **yes** | opaque origin, browser-enforced |
| `fragment` | HTML fragments | no | sanitizer + CSP, same origin — **deprecated, see §7** |

---

## 1. The idea

The old model asked the host to accept plugin markup into its own origin and then remove
the dangerous parts with a sanitizer. That conflates two different things in one blob:

- **content** — what the plugin wants to draw, and
- **authority** — what the plugin is thereby able to *do* to your session.

A sanitizer is what you need when those two travel together: an allowlist parser standing
between hostile input and your cookie, which has to be right every single time. Parsers
fail open.

`sandboxed` separates them instead.

- **Rendering** is the plugin's problem, in an origin where it holds no authority.
- **Authority** is the host's problem, granted explicitly through a narrow typed bridge.

Once they are separate, plugin JavaScript stops being dangerous, because there is nothing
in reach for it to abuse. The plugin ships whatever it likes — React, canvas, drag and drop,
a WASM blob — and the browser, not a regex, is what keeps it away from your library.

```
┌─ Bindery page (your origin: cookie, CSRF token, API access) ─────────────┐
│                                                                          │
│  plugin-frame.js  ◄──── postMessage bridge ────►  ┌───────────────────┐  │
│  · validates every message                        │ <iframe sandbox>  │  │
│  · scopes paths to /plugins/{name}/ui/            │ origin: opaque    │  │
│  · attaches the CSRF token                        │ plugin's own JS   │  │
│                                                   └───────────────────┘  │
└──────────────────────────────────────────────────────────────────────────┘
```

The frame is served `<iframe sandbox="allow-scripts allow-forms allow-popups allow-downloads">`
— note what is **absent**: `allow-same-origin`. Without it the document is assigned an
*opaque* origin, and the browser therefore refuses it:

- `document.cookie` — empty, always. Your session cookie is unreachable.
- `window.parent` DOM access — throws. Same-origin policy, enforced by the browser.
- `fetch('/api/library')` — a cross-origin request from origin `null`; Bindery sends no CORS
  headers, so it is blocked. Reinforced by `connect-src 'none'` in the frame's CSP.
- `localStorage` / `sessionStorage` / IndexedDB — access denied on an opaque origin.

None of that depends on Bindery parsing anything correctly.

---

## 2. Declaring UI in the manifest

```jsonc
{
  "ui": {
    "mode": "sandboxed",
    "entry": "/",
    "nav": [
      { "label": "Chapters", "path": "/", "icon": "book" },
      { "label": "Sources",  "path": "/sources", "icon": "settings", "section": "settings" }
    ]
  }
}
```

| Field | Required | Notes |
|---|---|---|
| `mode` | no | `declarative` (default), `sandboxed`, or `fragment`. An unrecognized value is treated as `declarative`. |
| `entry` | no | Path loaded when the user opens the plugin with no path. Default `/`. |
| `nav` | no | Entries Bindery links in its own chrome, pointing at `/plugins/{name}/ui{path}`. |

Anything not declared in `nav` is still reachable — it just is not linked from the chrome.

A host that does not implement `sandboxed` falls back to `declarative`, so declaring it is
safe against an older Bindery: the plugin loses its UI, not its function.

---

## 3. What the host serves

Bindery reverse-proxies `/plugins/{name}/ui/{**rest}` to the plugin's
`/bindery/v1/ui/{**rest}`, forwarding method, query string, and body.

For `sandboxed`, the proxy is a **byte pipe**. It does not sanitize, rewrite, or inspect the
body. It does enforce:

- an allowlist of response content types — `text/html`, `text/css`, `text/javascript`,
  `application/javascript`, `application/json`, `image/*`, `font/*`, `text/event-stream`.
  Anything else is refused rather than forwarded.
- the size and time caps in `PLUGIN-PROTOCOL.md` §9.
- `Cache-Control: no-store`.
- a per-response CSP for the frame document (§5).

A `sandboxed` plugin therefore serves a **complete document**, not a fragment: `<!doctype
html>`, `<head>`, its own `<style>` and `<script>`. Nothing wraps it.

It may serve its own subresources from its own mount point —
`/plugins/{name}/ui/app.js`, `/plugins/{name}/ui/style.css` — and they will be forwarded.

### Relative URLs are fine here

Unlike `fragment`, a sandboxed document is loaded *at* its mount path, so ordinary relative
URLs resolve correctly and `X-Bindery-Base` is informational rather than load-bearing. The
header is still sent, and `<base href>` is blocked by CSP, so prefer relative paths.

---

## 4. The bridge

`postMessage` is the only channel through which a sandboxed plugin can reach anything of
Bindery's. Everything crossing it is a plain JSON object carrying `bindery: 1`.

Because the frame's origin is opaque, the parent posts with `targetOrigin: "*"` — there is
no origin string that could be named instead. The parent validates inbound messages by
checking `event.source` is the frame's `contentWindow`, which is the identity that matters.

### Frame → parent

| `type` | Payload | Effect |
|---|---|---|
| `ready` | — | Frame is initialized. Parent replies with `init`. |
| `resize` | `height` (number) | Parent sets the iframe's height. Clamped to 15 000 px. |
| `request` | `id`, `method`, `path`, `body?`, `contentType?` | Parent performs an HTTP request **to the plugin's own mount point** and replies `response`. |
| `notify` | `level`, `message` | Parent shows a toast in Bindery's chrome. Text only. |
| `navigate` | `path` | Parent updates the browser URL and nav highlight. No page load. |

### Parent → frame

| `type` | Payload |
|---|---|
| `init` | `plugin`, `base`, `theme`, `csrf: false` — the token is never sent into the frame |
| `response` | `id`, `status`, `contentType`, `body` |
| `error` | `id`, `message` |
| `theme` | `theme` — sent when the user switches light/dark |

### What the parent enforces on `request`

This is the security-critical surface, and it is deliberately small enough to read in one
sitting:

1. `path` **MUST** resolve, after URL parsing, to a path beginning with the plugin's own
   `/plugins/{name}/ui/` prefix. Absolute URLs, protocol-relative URLs, `..` traversal, and
   backslashes are rejected outright — not normalized.
2. `method` **MUST** be one of `GET`, `POST`, `PUT`, `PATCH`, `DELETE`.
3. The CSRF token is attached **by the parent**. It is never given to the frame, so a plugin
   cannot leak it and cannot get CSRF wrong.
4. Request and response bodies are size-capped; concurrent in-flight requests are capped.
5. The response is returned to the frame as a string. The parent does not interpret it.

A plugin asking for `/api/library`, `https://evil.test/`, or `/plugins/other-plugin/ui/` is
refused by the parent. The blast radius of a compromised plugin image is *its own backend* —
which it already controlled.

### Reference frame-side client

About twenty lines, and a plugin may simply copy them:

```js
const pending = new Map();
let seq = 0;

export function request(method, path, body) {
  const id = String(++seq);
  parent.postMessage({ bindery: 1, type: "request", id, method, path, body }, "*");
  return new Promise((resolve, reject) => pending.set(id, { resolve, reject }));
}

window.addEventListener("message", (e) => {
  const msg = e.data;
  if (!msg || msg.bindery !== 1) return;
  const entry = msg.id && pending.get(msg.id);
  if (!entry) return;
  pending.delete(msg.id);
  msg.type === "response" ? entry.resolve(msg) : entry.reject(new Error(msg.message));
});

new ResizeObserver(() => {
  parent.postMessage(
    { bindery: 1, type: "resize", height: document.documentElement.scrollHeight }, "*");
}).observe(document.documentElement);

parent.postMessage({ bindery: 1, type: "ready" }, "*");
```

---

## 5. Security

The frame document is served with its own Content-Security-Policy, replacing the host page's:

```
default-src 'none'; script-src 'unsafe-inline' 'unsafe-eval' <origin>;
style-src 'unsafe-inline' <origin>; img-src data: blob: <origin>; font-src data: <origin>;
media-src data: blob: <origin>; connect-src 'none'; form-action 'none';
frame-ancestors 'self'; base-uri 'none'; object-src 'none'; frame-src 'none'
```

`<origin>` is Bindery's own origin, which is what lets a plugin load subresources from its
mount point. Note the three that matter most:

- **`connect-src 'none'`** — no `fetch`, no `XMLHttpRequest`, no WebSocket, no
  `EventSource`, no `sendBeacon`. The bridge is not merely the *recommended* way out of the
  frame; it is the only one. A plugin cannot exfiltrate to a third party even if it wants to.
- **`frame-ancestors 'self'`** — the frame may only be embedded by Bindery. This replaces
  the global `X-Frame-Options: DENY` on this route only.
- **`'unsafe-inline'` is deliberate here** and carries none of its usual meaning. It is
  scoped to a document with an opaque origin, no network egress, and no access to anything
  of Bindery's. Injecting script into a context that holds no authority achieves nothing.

The host page keeps its strict policy unchanged — no `unsafe-inline` anywhere, which is why
Bindery's own scripts and styles remain external files.

**Also:**

- Plugin UI routes require an authenticated Bindery session. Feed tokens do **not** grant UI
  access; they are for OPDS only.
- **A second, narrowly scoped cookie makes the frame work at all.** Bindery's session cookie
  is `SameSite=Lax`, and a request initiated by an opaque origin has no site, so the browser
  treats it as cross-site and withholds it. The frame's first load is fine — the parent
  initiates that — but every subresource the plugin then asks for would arrive
  unauthenticated. Bindery therefore issues `bindery.frame` (`SameSite=None; Secure;
  HttpOnly; Path=/plugins`) when the plugin page renders. Widening the *session* cookie to
  `SameSite=None` instead would have traded a plugin inconvenience for a site-wide CSRF
  weakening. Because `SameSite=None` requires `Secure`, sandboxed plugin UIs need HTTPS in
  any deployment that has authentication switched on.
- Responses are never cached by the browser.
- The proxy does not buffer, so a plugin may stream SSE — though with `connect-src 'none'`,
  SSE is only useful to a `fragment` plugin.
- `allow-downloads` is granted so a plugin can hand the user a file; `allow-modals` is not.
- `allow-popups` is granted, but `allow-popups-to-escape-sandbox` is **not** — anything the
  frame opens inherits the same restrictions.

### The residual risk, stated plainly

The bridge is now the security-critical surface. It is roughly a hundred lines with a
five-item allowlist, versus a 14 KB HTML parser that had to correctly reject every vector
anyone will ever invent. That is the trade: a smaller, auditable, non-parsing boundary in
place of a larger one that fails open.

What a malicious plugin image can still do is what it could always do — lie to you about
what it downloaded. Nothing here changes that, and nothing can.

---

## 6. Styling

Bindery exposes its design system as a stylesheet the frame may link:

```html
<link rel="stylesheet" href="/css/bindery.css">
```

That is a deliberate, versioned, public API: the `--bnd-*` custom properties and the `.bnd-*`
utility classes. A plugin that uses them inherits light/dark mode and any future restyle for
free. A plugin that would rather ship its own CSS is free to; it is in its own document.

The current theme arrives in `init.theme` and on every `theme` message, so a plugin that
draws its own chrome can follow the host.

---

## 7. `fragment` mode is deprecated

`fragment` still works and is still specified — the sanitizer, the CSP, and the XSS
conformance gate remain in force, unchanged, and the reference FanFicFare plugin still uses
it. It is deprecated because its safety rests on the sanitizer being perfect, and
`sandboxed` does not need anything to be perfect.

- **New plugins SHOULD use `sandboxed`** (or `declarative`, which is safer still and needs
  no code at all).
- **Existing `fragment` plugins keep working.** No migration is forced and no
  `protocolVersion` bump is required — `sandboxed` is an additive enum value.
- The `fragment` rules are unchanged and still enforced: no JavaScript in any form, no CSS,
  every URL built from `X-Bindery-Base`, and the host sanitizes everything it emits.
- `iframe` was the earlier name for a specified-but-unbuilt tier. `sandboxed` is what got
  built; `iframe` is accepted as a deprecated alias for it.

Removing `fragment` would be a breaking change and would bump `protocolVersion` to 2. That
is a decision for a later release, not a side effect of this one.

---

## 8. Conformance

`tests/conformance/` exercises a plugin's UI surface without Bindery running.

For `sandboxed`:

- every declared `nav` path returns 200 `text/html`
- responses are complete documents, not fragments
- the document does not depend on `connect-src` (no `fetch`/`XMLHttpRequest` against the
  host origin) — the bridge is the only egress, and a plugin relying on `fetch` will fail at
  runtime in a way the suite catches at build time
- no subresource is loaded from a third-party origin, which CSP would block
- content types are within the proxy's allowlist

For `fragment`, unchanged and still a merge gate:

- fragments are well-formed and contain no `<html>`/`<head>`/`<body>`
- known XSS vectors from `vectors/xss.json` are neutralized
- declared `nav` paths all return 200
- `X-Bindery-Base` is honored
