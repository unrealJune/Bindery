# Plugin UIs

Status: **normative for protocol v1.**

A plugin may contribute real HTML to the Bindery interface. This document is the contract
for how, and — more importantly — the constraints that make it safe.

---

## The shape of it

Bindery owns the page. The shell, navigation, stylesheet, and htmx runtime are Bindery's.
A plugin contributes **fragments**: HTML that gets swapped into a region of a Bindery page.

```
GET /plugins/fanficfare/ui/search?q=hobbit          (Bindery, same origin)
        │
        │  reverse proxy, method + query + body forwarded
        ▼
GET /bindery/v1/ui/search?q=hobbit                  (plugin container)
        │
        │  returns an HTML fragment — not a full document
        ▼
   sanitize → inject CSRF wrapper → htmx swaps it into the shell
```

The plugin never serves `<html>`, `<head>`, or `<body>`. It returns the inside of a `<div>`.

---

## Declaring UI in the manifest

```jsonc
{
  "ui": {
    "mode": "fragment",
    "nav": [
      { "label": "AO3 Search", "path": "/search", "icon": "search" },
      { "label": "personal.ini", "path": "/config/ini", "section": "settings" }
    ]
  }
}
```

Bindery renders navigation entries pointing at `/plugins/{name}/ui{path}`. Anything not
declared in `nav` is still reachable — it just isn't linked from the chrome.

`mode` is one of:

| mode | Plugin ships | Isolation |
|---|---|---|
| `declarative` | nothing — Bindery renders forms from the config schema | total |
| `fragment` | HTML fragments | sanitizer + CSP, same origin |
| `iframe` | a full document, its own JS and CSS | separate origin, sandboxed |

---

## Rule 1 — no plugin JavaScript

**A `fragment` plugin may not ship JavaScript.** Not inline, not `<script src>`, not `on*`
attributes, not `javascript:` URLs. The sanitizer strips all of it and the CSP would block
what survives.

This sounds more limiting than it is. Bindery's htmx runtime is already loaded in the shell
and `hx-*` attributes are explicitly allowed through the sanitizer, so plugin HTML gets:

- forms that submit without a page load (`hx-post`, `hx-target`, `hx-swap`)
- live polling (`hx-trigger="every 2s"`)
- server-sent progress (`hx-ext="sse"`)
- inline validation, lazy loading, infinite scroll, optimistic UI

That covers essentially every plugin UI anyone actually wants to build. If a plugin truly
needs its own JS, that is what `mode: "iframe"` is for — and the tradeoff is explicit
rather than smuggled in.

## Rule 2 — build URLs from `X-Bindery-Base`

Every proxied request carries:

```
X-Bindery-Base: /plugins/fanficfare/ui
X-Bindery-Csrf: <token>
Authorization: Bearer <host→plugin shared secret>
```

The plugin prefixes its own links and htmx targets with the base value:

```html
<form hx-post="/plugins/fanficfare/ui/search" hx-target="#fff-results">
  <input name="q" placeholder="Search AO3…">
</form>
<div id="fff-results"></div>
```

Bindery **does not rewrite attributes**. Server-side rewriting of `hx-get` / `href` /
`action` looks tidy in a demo and then quietly breaks on the one relative URL in the one
attribute nobody thought about. The header is boring and total.

## Rule 3 — style with tokens, not stylesheets

A `fragment` plugin ships no CSS. Bindery exposes its design system as CSS custom
properties and a small set of utility classes, documented and treated as API:

```
--bnd-bg  --bnd-surface  --bnd-text  --bnd-muted  --bnd-accent  --bnd-danger
--bnd-radius  --bnd-gap  --bnd-font
.bnd-card  .bnd-btn  .bnd-btn-primary  .bnd-input  .bnd-list  .bnd-row  .bnd-badge
```

Plugin markup using these inherits light/dark mode and any future restyle for free.
`<style>` blocks are stripped; a narrow `style=""` attribute allowlist (layout properties
only) survives.

---

## Security

Plugin HTML rendered same-origin is stored XSS by construction. This is the real cost of
Tier 2 and it is worth being blunt about: a plugin that only downloads EPUBs never touches
your session cookie. A plugin that injects HTML into Bindery's origin is one sanitizer bug
away from doing exactly that.

Three layers, all required:

**1. Allowlist sanitizer.** Use a maintained library (`HtmlSanitizer`, AngleSharp-backed) —
do not hand-roll. Configuration:

- allow structural, text, table, form, and image elements
- strip `<script>`, `<style>`, `<iframe>`, `<object>`, `<embed>`, `<link>`, `<meta>`, `<base>`
- strip every `on*` attribute
- allow `hx-*`, `data-*`, `id`, `class`, `name`, `value`, `type`, `placeholder`, `for`
- allow `href`/`src` only with scheme `https:` or a path beginning with the plugin's own
  base — no `javascript:`, no `data:` except `data:image/*`
- `id` and `name` are namespace-prefixed on output to prevent a plugin colliding with
  Bindery's own DOM ids

**2. Content-Security-Policy**, enforced on every page that can host a fragment:

```
default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' https: data:;
frame-ancestors 'none'; base-uri 'none'; object-src 'none'
```

No `unsafe-inline`. Bindery's own scripts and styles are external files.

**3. CSRF.** Bindery wraps every proxied fragment in a container carrying the antiforgery
token, so htmx requests originating inside plugin HTML are covered automatically:

```html
<div id="plugin-ui" hx-headers='{"X-CSRF-TOKEN":"…"}'>
  <!-- sanitized plugin fragment -->
</div>
```

The plugin does nothing and cannot get it wrong. Bindery validates the token on every
non-GET request to `/plugins/{name}/ui/*`.

**Also:**

- Proxy responses are `text/html` only. Any other content type is rejected, not forwarded.
- Fragment responses are size-capped and time-capped; a plugin cannot hang a page render.
- Responses are never cached by the browser (`Cache-Control: no-store`).
- The proxy does not buffer, so a plugin may stream SSE for live progress.
- Plugin UI routes require an authenticated Bindery session. Feed tokens do **not** grant
  UI access — they are for OPDS only.

---

## `mode: "iframe"`

For a plugin that genuinely needs its own JavaScript.

Served into `<iframe sandbox="allow-forms allow-popups">` with **no** `allow-same-origin`,
ideally from a distinct origin (`plugins.<host>` or a per-plugin subdomain) so that even a
sandbox escape lands nowhere useful. The plugin serves a complete document and ships
whatever it likes.

Costs: no htmx interop with the parent, awkward auto-sizing, and styling that will not match
unless the plugin explicitly pulls Bindery's stylesheet. Use it when the alternative is
weakening the sanitizer — never weaken the sanitizer.

---

## Conformance

`tests/conformance/` exercises a plugin's UI surface without Bindery running:

- fragments are well-formed and contain no `<html>`/`<head>`/`<body>`
- nothing survives sanitization that shouldn't (the suite feeds known XSS vectors through
  and asserts they are neutralized)
- declared `nav` paths all return 200
- `X-Bindery-Base` is honored — the suite asserts emitted URLs carry the supplied prefix
