"""Fragment UI.

Three rules, all enforced by the conformance suite:

1. No JavaScript. Interactivity is Bindery's htmx runtime via `hx-*` attributes.
2. No CSS. Styling is Bindery's `--bnd-*` tokens and `.bnd-*` utility classes.
3. Every URL is built from the `X-Bindery-Base` header, never hardcoded.

Everything user-supplied goes through `esc()` before it touches markup. The host
sanitizes what comes back, but a plugin that relies on that is a plugin that breaks the
day the sanitizer has a gap.
"""

from __future__ import annotations

from html import escape
from typing import Optional

from . import fff
from .manifest import fanficfare_version, site_examples

MAX_SITES_SHOWN = 40


def esc(value) -> str:
    return escape(str(value if value is not None else ""), quote=True)


# ---------------------------------------------------------------- home


def home(base: str) -> str:
    sites = site_examples()
    return f"""
<div class="bnd-card">
  <h2>FanFicFare</h2>
  <p class="bnd-muted">
    FanFicFare {esc(fanficfare_version())} &middot; {len(sites)} supported sites.
    Paste a story URL anywhere in Bindery and it will be routed here automatically.
  </p>
</div>

<div class="bnd-card">
  <h3>Will this URL work?</h3>
  <p class="bnd-muted">Checked offline against the installed adapters. No request is made to the site.</p>
  <form class="bnd-row" hx-get="{esc(base)}/check" hx-target="#fff-check" hx-swap="innerHTML">
    <input class="bnd-input" type="url" name="url" placeholder="https://archiveofourown.org/works/12345"
           aria-label="Story URL">
    <button class="bnd-btn bnd-btn-primary" type="submit">Check</button>
  </form>
  <div id="fff-check"></div>
</div>

<div class="bnd-card">
  <h3>Where to go next</h3>
  <ul class="bnd-list">
    <li class="bnd-row"><a href="{esc(base)}/sites">Browse the supported sites</a></li>
    <li class="bnd-row"><a href="{esc(base)}/ini">Validate your personal.ini</a></li>
  </ul>
</div>
""".strip()


def check(base: str, url: Optional[str]) -> str:
    url = (url or "").strip()
    if not url:
        return '<p class="bnd-muted">Enter a URL above.</p>'

    result = fff.normalize(url)
    if not result:
        return f"""
<div class="bnd-card">
  <p><span class="bnd-badge">Not supported</span></p>
  <p>No installed FanFicFare adapter claims <code>{esc(url)}</code>.</p>
  <p class="bnd-muted">Check the <a href="{esc(base)}/sites">supported sites</a>, or the URL may
     point at a chapter list or author page rather than a story.</p>
</div>
""".strip()

    normalized, domain = result
    changed = ""
    if normalized != url:
        changed = (
            f'<p class="bnd-muted">Bindery will normalize this to '
            f"<code>{esc(normalized)}</code> before downloading.</p>"
        )
    return f"""
<div class="bnd-card">
  <p><span class="bnd-badge">Supported</span> handled by the <strong>{esc(domain)}</strong> adapter.</p>
  {changed}
</div>
""".strip()


# ---------------------------------------------------------------- sites


def sites(base: str, query: Optional[str]) -> str:
    return f"""
<div class="bnd-card">
  <h2>Supported sites</h2>
  <p class="bnd-muted">
    Read out of the installed FanFicFare {esc(fanficfare_version())} at startup, not from a
    list maintained here. Updating the plugin image updates this page.
  </p>
  <form class="bnd-row" hx-get="{esc(base)}/sites/list" hx-target="#fff-sites-list"
        hx-swap="innerHTML" hx-trigger="submit, keyup changed delay:300ms from:find input">
    <input class="bnd-input" type="search" name="q" value="{esc(query)}"
           placeholder="Filter by domain" aria-label="Filter sites">
    <button class="bnd-btn" type="submit">Filter</button>
  </form>
  <div id="fff-sites-list">{site_list(query)}</div>
</div>
""".strip()


def site_list(query: Optional[str]) -> str:
    term = (query or "").strip().lower()
    matched = [
        (domain, examples)
        for domain, examples in site_examples()
        if not term or term in domain.lower()
    ]
    if not matched:
        return f'<p class="bnd-muted">No supported site matches {esc(term)}.</p>'

    shown = matched[:MAX_SITES_SHOWN]
    rows = "".join(
        f'<li class="bnd-row"><strong>{esc(domain)}</strong>'
        f'<code>{esc(examples[0]) if examples else ""}</code></li>'
        for domain, examples in shown
    )
    more = ""
    if len(matched) > len(shown):
        more = (
            f'<p class="bnd-muted">Showing {len(shown)} of {len(matched)} matches. '
            "Narrow the filter to see the rest.</p>"
        )
    return f'<ul class="bnd-list">{rows}</ul>{more}'


# ---------------------------------------------------------------- personal.ini


def ini(base: str) -> str:
    return f"""
<div class="bnd-card">
  <h2>personal.ini</h2>
  <p class="bnd-muted">
    Bindery stores this for you &mdash; it is the <strong>personal.ini overrides</strong> field in
    this plugin's settings. Paste it here first to check it parses and that its sections name
    sites FanFicFare actually knows.
  </p>
  <form hx-post="{esc(base)}/ini/validate" hx-target="#fff-ini-result" hx-swap="innerHTML">
    <textarea class="bnd-input" name="ini" rows="14" spellcheck="false"
              aria-label="personal.ini contents"
              placeholder="[archiveofourown.org]&#10;username:someone&#10;is_adult:true"></textarea>
    <div class="bnd-row">
      <button class="bnd-btn bnd-btn-primary" type="submit">Validate</button>
    </div>
  </form>
  <div id="fff-ini-result"></div>
</div>

<div class="bnd-card">
  <h3>Options worth knowing</h3>
  <ul class="bnd-list">
    <li class="bnd-row"><code>is_adult:true</code><span class="bnd-muted">Skip adult-content gates</span></li>
    <li class="bnd-row"><code>include_images:true</code><span class="bnd-muted">Embed illustrations</span></li>
    <li class="bnd-row"><code>keep_summary_html:true</code><span class="bnd-muted">Preserve formatting in summaries</span></li>
    <li class="bnd-row"><code>slow_down_sleep_time:2</code><span class="bnd-muted">Be gentler with a rate-limiting site</span></li>
    <li class="bnd-row"><code>use_ssl_default_seconds:120</code><span class="bnd-muted">Raise timeouts on a slow site</span></li>
  </ul>
  <p class="bnd-muted">
    Full reference:
    <a href="https://github.com/JimmXinu/FanFicFare/wiki/DefaultsIni">FanFicFare's defaults.ini documentation</a>.
  </p>
</div>
""".strip()


def ini_result(text: str) -> str:
    if not (text or "").strip():
        return '<p class="bnd-muted">Nothing to validate.</p>'

    problems = fff.validate_ini(text)
    if not problems:
        return (
            '<p><span class="bnd-badge">Parses cleanly</span> '
            "every section names a site FanFicFare recognizes.</p>"
        )
    items = "".join(f'<li class="bnd-row">{esc(problem)}</li>' for problem in problems)
    return f'<p><span class="bnd-badge">{len(problems)} problem(s)</span></p><ul class="bnd-list">{items}</ul>'


def not_found() -> str:
    return '<div class="bnd-card"><p>No such page in this plugin.</p></div>'
