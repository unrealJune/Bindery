"""The sandboxed UI: complete HTML documents plus a small JSON API behind the bridge.

`ui.mode: "sandboxed"` (see `docs/PLUGIN-UI.md`) is the opposite of the FanFicFare plugin's
`fragment` mode. Here Hedgerow ships a *whole document* with its own JavaScript and CSS,
loaded into an opaque-origin iframe. It holds no authority over Bindery, so its JS is not
dangerous — which is exactly what lets the flagship feature, drag-and-drop chapter
reordering, exist at all.

The document's CSP sets `connect-src 'none'`, so the frame cannot `fetch`. Every call to the
plugin's own backend goes through the postMessage bridge in `app.js`; the JSON handlers it
talks to live in `main.py`. Even here, everything user-supplied is escaped before it reaches
markup — the frame is sandboxed, but defense in depth is cheap and the conformance suite
checks reflection.
"""

from __future__ import annotations

from html import escape


def esc(value) -> str:
    return escape(str(value if value is not None else ""), quote=True)


def shell() -> str:
    """The single-page shell served for every UI route.

    One document serves both `/` and `/sources`; `app.js` chooses the view from the path it
    was loaded at. Subresources are referenced relatively so they resolve under the plugin's
    own mount point in either location; `bindery.css` is the host's public design-token API.
    """
    return """<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Hedgerow</title>
  <link rel="stylesheet" href="/css/bindery.css">
  <link rel="stylesheet" href="style.css">
</head>
<body>
  <div id="app" class="hg-app">
    <p class="hg-muted">Loading&hellip;</p>
  </div>
  <script src="app.js"></script>
</body>
</html>
"""
