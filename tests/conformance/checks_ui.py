"""Plugin UI conformance checks — docs/PLUGIN-UI.md.

These run only for plugins declaring a UI tier. Two tiers are checked, and what makes a
plugin conformant differs sharply between them:

`sandboxed` (recommended) — the plugin serves a complete document into an opaque-origin
iframe. It may ship whatever JavaScript it likes, so there is nothing to forbid. What is
checked instead is that it will actually *work* inside the frame's CSP: no `fetch`, no
third-party subresources, and content types the proxy will forward.

`fragment` (deprecated) — the plugin's markup is inlined into Bindery's own origin. These
checks are the load-bearing ones and remain a merge gate: a fragment plugin that ships
JavaScript, emits relative URLs, or reflects input raw is a plugin whose HTML the host
cannot safely host.
"""

from __future__ import annotations

import json
import os
import re
import urllib.parse

from harness import Ctx, Fail, Skip, check, expect, expect_status

BASE = "/plugins/{name}/ui"
MAX_FRAGMENT_BYTES = 512 * 1024
MAX_SANDBOXED_BYTES = 4 * 1024 * 1024

DOCUMENT_MARKERS = ("<!doctype", "<html", "<head", "<body", "</html>", "</body>")
SCRIPT_RE = re.compile(r"<\s*script", re.I)
EVENT_ATTR_RE = re.compile(r"""[\s"']on[a-z]+\s*=""", re.I)
HX_ON_RE = re.compile(r"hx-on", re.I)
JS_URL_RE = re.compile(r"""(?:href|src|action|formaction)\s*=\s*["']?\s*javascript:""", re.I)
JS_VALS_RE = re.compile(r"""hx-(?:vals|headers)\s*=\s*["']?\s*js:""", re.I)
URL_ATTR_RE = re.compile(
    r"""\b(href|action|hx-get|hx-post|hx-put|hx-patch|hx-delete)\s*=\s*(?:"([^"]*)"|'([^']*)')""",
    re.I,
)

# What the frame's `connect-src 'none'` makes impossible. A plugin reaching for any of
# these is one that will fail at runtime, so it is caught here instead.
EGRESS_RE = re.compile(
    r"""\b(?:fetch\s*\(|XMLHttpRequest|EventSource|WebSocket|navigator\.sendBeacon)""")
SUBRESOURCE_RE = re.compile(
    r"""\b(?:src|href)\s*=\s*(?:"([^"]*)"|'([^']*)')""", re.I)

# The proxy's allowlist, from PLUGIN-PROTOCOL.md §6.1.
SANDBOXED_TYPES = {
    "text/html", "text/css", "text/javascript", "application/javascript",
    "application/json", "text/plain", "image/svg+xml", "text/event-stream",
}


def _allowed_sandboxed_type(media_type: str) -> bool:
    media_type = (media_type or "").lower()
    return (
        media_type in SANDBOXED_TYPES
        or media_type.startswith("image/")
        or media_type.startswith("font/")
    )


TAG_RE = re.compile(r"<[a-zA-Z!/][^>]*>", re.S)
QUOTED_VALUE_RE = re.compile("\"[^\"]*\"|'[^']*'")


def markup_only(body: str) -> str:
    """Just the tags, so escaped text is not mistaken for markup.

    A correctly escaped reflection of `<img onerror=x>` still leaves the literal
    characters `onerror=` in the body. It is only dangerous inside a real tag.
    """
    return "\n".join(TAG_RE.findall(body))


def attribute_names_only(body: str) -> str:
    """Tags with quoted attribute values blanked, leaving attribute names intact.

    `value="&lt;img onerror=x&gt;"` is a correctly escaped reflection and must not read as
    an event handler, while `<img onerror="x">` still must. What separates them is whether
    the text sits inside a quoted value or names an attribute — so blank the values and
    keep the names.
    """
    return QUOTED_VALUE_RE.sub('""', markup_only(body))


def _is_fragment_plugin(ctx: Ctx) -> bool:
    return ctx.ui_mode == "fragment"


def _is_sandboxed_plugin(ctx: Ctx) -> bool:
    # 'iframe' was the tier's name before it was built; the host accepts it as an alias.
    return ctx.ui_mode in ("sandboxed", "iframe")


def _base(ctx: Ctx) -> str:
    return BASE.format(name=ctx.manifest.get("name", "plugin"))


def _nav_paths(ctx: Ctx) -> list:
    nav = (ctx.manifest.get("ui") or {}).get("nav") or []
    return [entry["path"] for entry in nav if isinstance(entry.get("path"), str)]


def _get_fragment(ctx: Ctx, path: str, *, query: str = "", expect_html: bool = True):
    url = f"/bindery/v1/ui{path}"
    if query:
        url += ("&" if "?" in url else "?") + query
    resp = ctx.client.request(
        "GET",
        url,
        headers={"X-Bindery-Base": _base(ctx), "X-Bindery-Csrf": "conformance-token"},
        timeout=15,
    )
    expect_status(resp, 200)
    if expect_html:
        expect(
            resp.content_type in ("text/html", "text/event-stream"),
            f"{url}: the proxy forwards text/html only, got {resp.content_type!r}",
        )
    return resp


def _entry_paths(ctx: Ctx) -> list:
    """Every UI path the host might load: the declared entry plus every nav path."""
    ui = ctx.manifest.get("ui") or {}
    paths = [ui.get("entry") or "/"]
    for path in _nav_paths(ctx):
        if path not in paths:
            paths.append(path)
    return paths


@check("ui.nav-paths-resolve", group="ui", requires=_is_fragment_plugin)
def ui_nav_paths_resolve(ctx: Ctx) -> None:
    """Every path declared in ui.nav returns 200 text/html."""
    paths = _nav_paths(ctx)
    if not paths:
        raise Skip("plugin declares no ui.nav entries")
    for path in paths:
        _get_fragment(ctx, path)


@check("ui.fragments-are-fragments", group="ui", requires=_is_fragment_plugin)
def ui_fragments_are_fragments(ctx: Ctx) -> None:
    """No nav response is a full document."""
    paths = _nav_paths(ctx)
    if not paths:
        raise Skip("plugin declares no ui.nav entries")
    for path in paths:
        body = _get_fragment(ctx, path).text.lower()
        for marker in DOCUMENT_MARKERS:
            expect(marker not in body, f"{path}: fragment contains {marker!r} — plugins return the inside of a div")


@check("ui.size-capped", group="ui", requires=_is_fragment_plugin)
def ui_size_capped(ctx: Ctx) -> None:
    """No fragment exceeds the host's 512 KiB cap."""
    for path in _nav_paths(ctx) or []:
        resp = _get_fragment(ctx, path)
        expect(
            len(resp.body) <= MAX_FRAGMENT_BYTES,
            f"{path}: fragment is {len(resp.body)} bytes; the host truncates above {MAX_FRAGMENT_BYTES}",
        )


@check("ui.no-javascript", group="ui", requires=_is_fragment_plugin)
def ui_no_javascript(ctx: Ctx) -> None:
    """Fragments carry no JavaScript in any form. This is the rule the tier rests on."""
    paths = _nav_paths(ctx)
    if not paths:
        raise Skip("plugin declares no ui.nav entries")
    for path in paths:
        raw = _get_fragment(ctx, path).text
        tags, names = markup_only(raw), attribute_names_only(raw)
        expect(SCRIPT_RE.search(tags) is None, f"{path}: fragment contains a <script> element")
        match = EVENT_ATTR_RE.search(names)
        expect(match is None, f"{path}: fragment contains an inline event handler ({match.group(0).strip() if match else ''})")
        expect(HX_ON_RE.search(names) is None, f"{path}: fragment uses hx-on, which is inline JavaScript")
        expect(JS_URL_RE.search(tags) is None, f"{path}: fragment contains a javascript: URL")
        expect(JS_VALS_RE.search(tags) is None, f"{path}: fragment uses the js: prefix on hx-vals/hx-headers")
        expect("<style" not in tags.lower(), f"{path}: fragments ship no CSS; use the --bnd-* design tokens")


@check("ui.honours-bindery-base", group="ui", requires=_is_fragment_plugin)
def ui_honours_bindery_base(ctx: Ctx) -> None:
    """Emitted URLs are built from X-Bindery-Base, because the host does not rewrite them."""
    paths = _nav_paths(ctx)
    if not paths:
        raise Skip("plugin declares no ui.nav entries")
    base = _base(ctx)
    offenders = []
    for path in paths:
        body = _get_fragment(ctx, path).text
        for match in URL_ATTR_RE.finditer(body):
            attr = match.group(1).lower()
            value = (match.group(2) if match.group(2) is not None else match.group(3)).strip()
            if value == "" or value.startswith("#"):
                continue
            if value.startswith("https://") or value.startswith("http://"):
                if attr != "href":
                    offenders.append(f"{path}: {attr}={value!r} points off-origin")
                continue
            if not value.startswith("/"):
                offenders.append(f"{path}: {attr}={value!r} is relative; build it from X-Bindery-Base")
            elif not value.startswith(base):
                offenders.append(f"{path}: {attr}={value!r} does not start with the supplied base {base!r}")
    if offenders:
        raise Fail("; ".join(offenders[:5]))


@check("ui.base-is-not-hardcoded", group="ui", requires=_is_fragment_plugin)
def ui_base_is_not_hardcoded(ctx: Ctx) -> None:
    """Changing X-Bindery-Base changes the emitted URLs."""
    paths = _nav_paths(ctx)
    if not paths:
        raise Skip("plugin declares no ui.nav entries")
    alt_base = "/alternate/mount/point"
    path = paths[0]
    resp = ctx.client.request(
        "GET",
        f"/bindery/v1/ui{path}",
        headers={"X-Bindery-Base": alt_base, "X-Bindery-Csrf": "conformance-token"},
        timeout=15,
    )
    expect_status(resp, 200)
    body = resp.text
    emitted = [
        (m.group(2) if m.group(2) is not None else m.group(3))
        for m in URL_ATTR_RE.finditer(body)
    ]
    local = [v for v in emitted if v.startswith("/")]
    if not local:
        raise Skip(f"{path} emits no local URLs to check")
    expect(
        all(v.startswith(alt_base) for v in local),
        f"{path}: with X-Bindery-Base={alt_base!r} the fragment still emitted {local[:3]} — the base is hardcoded",
    )


@check("ui.no-raw-reflection", group="ui", requires=_is_fragment_plugin)
def ui_no_raw_reflection(ctx: Ctx) -> None:
    """User input echoed into a fragment comes back escaped, never raw.

    The host sanitizes what a plugin emits, so a reflected vector is not directly
    exploitable — but a plugin that concatenates input into markup is one sanitizer gap
    away from being, and the fix belongs on both sides.
    """
    paths = _nav_paths(ctx)
    if not paths:
        raise Skip("plugin declares no ui.nav entries")
    offenders = []
    for path in paths:
        for vector in load_vectors():
            payload = vector["payload"]
            query = urllib.parse.urlencode({"q": payload, "query": payload})
            try:
                body = _get_fragment(ctx, path, query=query).text
            except Fail:
                continue  # a plugin is free to reject the input outright
            if payload.lower() in body.lower():
                offenders.append(f"{path}: reflected vector {vector['name']} verbatim")
                continue
            tags, names = markup_only(body), attribute_names_only(body)
            for pattern, scanned, label in (
                (SCRIPT_RE, tags, "a <script> element"),
                (EVENT_ATTR_RE, names, "an inline event handler"),
                (JS_URL_RE, tags, "a javascript: URL"),
                (HX_ON_RE, names, "an hx-on attribute"),
            ):
                if pattern.search(scanned):
                    offenders.append(f"{path}: vector {vector['name']} produced {label}")
                    break
    if offenders:
        raise Fail("; ".join(sorted(set(offenders))[:5]))


@check("ui.sandboxed-serves-document", group="ui", requires=_is_sandboxed_plugin)
def ui_sandboxed_serves_document(ctx: Ctx) -> None:
    """A sandboxed plugin serves complete documents, since nothing wraps them."""
    for path in _entry_paths(ctx):
        body = _get_fragment(ctx, path).text.lower()
        expect("<html" in body, f"{path}: sandboxed plugins serve a full document, not a fragment")
        expect("<body" in body, f"{path}: sandboxed document has no <body>")


@check("ui.sandboxed-content-types", group="ui", requires=_is_sandboxed_plugin)
def ui_sandboxed_content_types(ctx: Ctx) -> None:
    """Every response, including subresources, is a type the proxy will forward."""
    for path in _entry_paths(ctx):
        resp = _get_fragment(ctx, path, expect_html=False)
        expect(
            _allowed_sandboxed_type(resp.content_type),
            f"{path}: content type {resp.content_type!r} is outside the proxy's allowlist "
            "(PLUGIN-PROTOCOL.md §6.1) and will be refused, not forwarded",
        )
        for sub in _subresources(ctx, resp.text):
            sub_resp = ctx.client.request(
                "GET",
                f"/bindery/v1/ui{sub}",
                headers={"X-Bindery-Base": _base(ctx)},
                timeout=15,
            )
            if sub_resp.status != 200:
                continue
            expect(
                _allowed_sandboxed_type(sub_resp.content_type),
                f"{sub}: subresource content type {sub_resp.content_type!r} is outside the "
                "proxy's allowlist and will not be forwarded",
            )


@check("ui.sandboxed-size-capped", group="ui", requires=_is_sandboxed_plugin)
def ui_sandboxed_size_capped(ctx: Ctx) -> None:
    """No sandboxed response exceeds the host's 4 MiB cap."""
    for path in _entry_paths(ctx):
        resp = _get_fragment(ctx, path, expect_html=False)
        expect(
            len(resp.body) <= MAX_SANDBOXED_BYTES,
            f"{path}: response is {len(resp.body)} bytes; the host refuses above {MAX_SANDBOXED_BYTES}",
        )


@check("ui.sandboxed-uses-the-bridge", group="ui", requires=_is_sandboxed_plugin)
def ui_sandboxed_uses_the_bridge(ctx: Ctx) -> None:
    """No plugin code reaches for network egress the frame's CSP removes.

    `connect-src 'none'` means fetch, XMLHttpRequest, EventSource, WebSocket, and
    sendBeacon all fail inside the frame. A plugin depending on any of them is broken at
    runtime in a way that is much cheaper to catch here — the postMessage bridge in
    PLUGIN-UI.md §4 is the only way out.
    """
    offenders = []
    for path in _entry_paths(ctx):
        resp = _get_fragment(ctx, path, expect_html=False)
        sources = [(path, resp.text)]
        for sub in _subresources(ctx, resp.text):
            sub_resp = ctx.client.request(
                "GET",
                f"/bindery/v1/ui{sub}",
                headers={"X-Bindery-Base": _base(ctx)},
                timeout=15,
            )
            if sub_resp.status == 200:
                sources.append((sub, sub_resp.text))
        for where, text in sources:
            match = EGRESS_RE.search(text)
            if match:
                offenders.append(f"{where}: uses {match.group(0)!r}, which connect-src 'none' blocks")
    if offenders:
        raise Fail("; ".join(sorted(set(offenders))[:5]))


@check("ui.sandboxed-no-third-party-subresources", group="ui", requires=_is_sandboxed_plugin)
def ui_sandboxed_no_third_party_subresources(ctx: Ctx) -> None:
    """Subresources come from the plugin's own mount point, which is all CSP permits."""
    offenders = []
    for path in _entry_paths(ctx):
        body = _get_fragment(ctx, path, expect_html=False).text
        for match in SUBRESOURCE_RE.finditer(body):
            value = (match.group(1) if match.group(1) is not None else match.group(2)).strip()
            if value.startswith(("http://", "https://", "//")):
                offenders.append(f"{path}: {value!r} is off-origin and will be blocked by the frame's CSP")
    if offenders:
        raise Fail("; ".join(sorted(set(offenders))[:5]))


def _subresources(ctx: Ctx, body: str) -> list:
    """Plugin-served script/style/image paths, mapped back to /bindery/v1/ui paths.

    Both spellings are accepted: a document loaded at its mount point may use ordinary
    relative URLs, and one built from X-Bindery-Base will carry the prefix.
    """
    base = _base(ctx)
    found = []
    for match in SUBRESOURCE_RE.finditer(body):
        value = (match.group(1) if match.group(1) is not None else match.group(2)).strip()
        if not value or value.startswith(("http://", "https://", "//", "data:", "#", "mailto:")):
            continue
        if value.startswith(base):
            path = value[len(base):] or "/"
        elif value.startswith("/"):
            continue  # some other part of Bindery, e.g. /css/bindery.css — not the plugin's
        else:
            path = "/" + value
        if ".." in path:
            continue
        if path not in found:
            found.append(path)
    return found


@check("ui.absent-when-declarative", group="ui", requires=lambda ctx: ctx.ui_mode == "declarative")
def ui_absent_when_declarative(ctx: Ctx) -> None:
    """A declarative plugin exposes no UI endpoints; the host never proxies to it."""
    resp = ctx.client.request(
        "GET",
        "/bindery/v1/ui/",
        headers={"X-Bindery-Base": _base(ctx)},
        timeout=15,
    )
    if resp.status == 200:
        ctx.note("plugin serves /bindery/v1/ui/ but declares ui.mode 'declarative'; the host will never proxy it")


def load_vectors() -> list:
    """The shared XSS corpus, also consumed by the host's sanitizer tests."""
    path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "vectors", "xss.json")
    with open(path, "r", encoding="utf-8") as handle:
        return json.load(handle)["vectors"]
