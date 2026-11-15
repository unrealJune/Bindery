"""Turning a post's `.entry-content` into chapter XHTML for the EPUB.

Two jobs, in order:

1. **Strip the furniture.** Navigation links ("Next Chapter", "Table of Contents"), share
   buttons, related-post widgets, Patreon/Discord promo buttons, scripts, embeds, forms.
   Prose is never deleted for being promotional — a sentence that links to the author's
   Patreon keeps its words and loses only the link — but an *image* that is a link to
   Patreon is a button, and goes.
2. **Serialize through an allowlist.** Every element and attribute is either known-safe and
   emitted as well-formed XHTML, renamed to something that is, or unwrapped. The output is
   parseable as XML by construction, which is what an EPUB reader needs, and carries no
   script, event handler, or `javascript:` URL in any form.

Images are not fetched here. `image_urls` lists what the body references, and `serialize`
writes each kept `<img>` as `src="images/<hash>"` — a placeholder the EPUB builder resolves
once it knows which downloads succeeded (and with what extension).
"""

from __future__ import annotations

import hashlib
import html as html_module
import re
from typing import Dict, List, Optional

from . import site
from .fetch import split
from .dom import Node, squash

DROP = frozenset({
    "script", "style", "noscript", "template", "iframe", "object", "embed", "form", "input",
    "button", "select", "textarea", "svg", "math", "canvas", "video", "audio", "nav", "footer",
    "link", "meta", "head", "title", "map", "area", "source", "track", "dialog", "label",
    "fieldset", "legend", "output", "progress", "meter",
})

ALLOWED = frozenset({
    "p", "br", "hr", "em", "i", "strong", "b", "u", "s", "del", "ins", "sub", "sup", "small",
    "span", "div", "blockquote", "pre", "code", "kbd", "samp", "var", "h3", "h4", "h5", "h6",
    "ul", "ol", "li", "dl", "dt", "dd", "table", "thead", "tbody", "tfoot", "tr", "th", "td",
    "caption", "img", "a", "figure", "figcaption", "abbr", "cite", "q", "mark", "ruby", "rt",
    "rp", "bdi", "bdo", "time", "address",
})

RENAME = {
    "h1": "h3", "h2": "h3", "center": "div", "font": "span", "details": "div", "summary": "p",
    "article": "div", "section": "div", "main": "div", "header": "div", "aside": "div",
    "big": "span", "tt": "code", "strike": "s", "dfn": "i", "hgroup": "div", "address": "address",
}

VOID_OUT = frozenset({"br", "hr", "img"})

STYLE_PROPS = frozenset({"text-align", "font-style", "font-weight", "text-decoration",
                         "font-variant", "text-indent", "text-transform"})
_STYLE_VALUE = re.compile(r"^[a-zA-Z0-9 .%#,-]{1,60}$")

PROMO_HOSTS = re.compile(
    r"(?:^|\.)(?:patreon\.com|discord\.gg|discord\.com|discordapp\.com|ko-fi\.com|buymeacoffee\.com|"
    r"subscribestar\.(?:com|adult)|topwebfiction\.com|webfictionguide\.com|paypal\.(?:com|me)|"
    r"twitter\.com|x\.com|facebook\.com|tumblr\.com|reddit\.com|pinterest\.com|linkedin\.com)$", re.I)
PROMO_PATH = re.compile(r"patreon|discord|ko-?fi|subscribe|donate|vote", re.I)
TRACKERS = re.compile(r"(?:^|\.)(?:pixel\.wp\.com|stats\.wp\.com|gravatar\.com|feeds\.wordpress\.com|"
                      r"google-analytics\.com|googletagmanager\.com|doubleclick\.net|facebook\.com)$", re.I)

_XML_INVALID = re.compile("[\x00-\x08\x0b\x0c\x0e-\x1f￾￿]")


# ---------------------------------------------------------------- cleaning


def strip_furniture(content: Node, base_url: str) -> None:
    """Remove everything from a post body that is not the story. Mutates `content`."""
    for node in content.find_all(lambda n: True):
        if node.parent is None:
            continue  # already gone with an ancestor
        if node.tag in DROP or site._junk(node):
            node.remove()

    for anchor in content.find_all(lambda n: n.tag == "a"):
        if anchor.parent is None:
            continue
        href = site.absolute(base_url, anchor.get("href"))
        label = anchor.clean_text() or anchor.get("title")
        rels = anchor.get("rel").lower().split()
        if _is_nav(label, rels):
            parent = anchor.parent
            anchor.remove()
            _prune_upward(parent, content)
        elif href and _is_promo(href, base_url):
            if not label:
                parent = anchor.parent
                anchor.remove()  # an image-button: "Support me on Patreon" as a picture
                _prune_upward(parent, content)
            else:
                anchor.unwrap()  # words stay, the link goes

    for img in content.find_all(lambda n: n.tag == "img"):
        if img.parent is not None and image_src(img, base_url) is None:
            parent = img.parent
            img.remove()
            _prune_upward(parent, content)

    _trim_edges(content)


def _is_nav(label: str, rels: List[str]) -> bool:
    if "next" in rels or "prev" in rels:
        return True
    label = squash(label or "")
    return bool(label) and len(label) <= 40 and bool(site.NAV_ONLY_TEXT.match(label))


def _is_promo(url: str, base_url: str) -> bool:
    parts = split(url)
    if parts is None:
        return False
    host = (parts.hostname or "").lower()
    if PROMO_HOSTS.search(host):
        return True
    return site.same_site(url, base_url) and bool(PROMO_PATH.search(parts.path + "?" + parts.query))


_SEPARATORS = re.compile(r"[\s|•·\-–—/«‹←»›→<> ]+")


def _empty(node: Node) -> bool:
    """No words and no pictures. A lone `|` or `•` between two removed nav links is empty."""
    if node.tag in ("img", "hr", "br"):
        return False
    if _SEPARATORS.sub("", node.text()):
        return False
    return node.find(lambda n: n.tag in ("img", "hr")) is None


def _prune_upward(node: Optional[Node], stop: Node) -> None:
    while node is not None and node is not stop and _empty(node):
        parent = node.parent
        node.remove()
        node = parent


def _trim_edges(content: Node) -> None:
    """Drop leading and trailing breaks and empty blocks left behind by removed navigation."""
    for edge in (0, -1):
        while content.children:
            child = content.children[edge]
            if isinstance(child, str):
                if child.strip():
                    break
                content.children.pop(edge)
                continue
            if child.tag == "br" or (child.tag != "hr" and _empty(child)):
                child.remove()
                continue
            break


# ---------------------------------------------------------------- images


MAX_IMAGE_WIDTH = 1280  # an ereader screen; full-size uploads are routinely 4000px PNGs


def image_src(img: Node, base_url: str) -> Optional[str]:
    """The image URL to embed for an <img>, or None for trackers, spacers, and data: URIs.

    WordPress publishes every upload at several sizes and lists them in `srcset`. The largest
    one no wider than an ereader screen is the right one to embed: an original commissioned
    illustration can be 6 MB, and a book of them would not fit on some readers.
    """
    src = (_from_srcset(img.get("data-lazy-srcset") or img.get("srcset"))
           or img.get("data-lazy-src") or img.get("data-src") or img.get("src")
           or img.get("data-orig-file") or "").strip()
    if not src or src.startswith("data:"):
        return None
    url = site.absolute(base_url, src)
    if not url:
        return None
    parts = split(url)
    host = ((parts.hostname if parts is not None else "") or "").lower()
    if TRACKERS.search(host):
        return None
    if img.get("width") in ("0", "1") or img.get("height") in ("0", "1"):
        return None
    return url


def _from_srcset(srcset: str) -> Optional[str]:
    best = None
    for candidate in (srcset or "").split(","):
        parts = candidate.strip().split()
        if len(parts) != 2 or not parts[1].endswith("w") or not parts[1][:-1].isdigit():
            continue
        width = int(parts[1][:-1])
        if width <= MAX_IMAGE_WIDTH and (best is None or width > best[0]):
            best = (width, parts[0])
    return best[1] if best else None


def image_urls(content: Node, base_url: str) -> List[str]:
    seen, urls = set(), []
    for img in content.find_all(lambda n: n.tag == "img"):
        url = image_src(img, base_url)
        if url and url not in seen:
            seen.add(url)
            urls.append(url)
    return urls


def image_token(url: str) -> str:
    return hashlib.sha256(url.encode("utf-8")).hexdigest()[:20]


# ---------------------------------------------------------------- serialization


def serialize(content: Node, base_url: str, embed_images: bool = True) -> str:
    out: List[str] = []
    for child in content.children:
        _write(child, out, base_url, embed_images)
    body = "".join(out).strip()
    # What removed navigation leaves behind: a stack of spacer breaks and blank lines.
    body = re.sub(r"(?:<br/>\s*){3,}", "<br/><br/>", body)
    if "<pre" not in body:
        body = re.sub(r"\n\s*\n+", "\n", body)
    return body


def _write(node, out: List[str], base_url: str, embed_images: bool) -> None:
    if isinstance(node, str):
        out.append(_text(node))
        return
    tag = node.tag
    if tag in DROP:
        return
    tag = RENAME.get(tag, tag)
    if tag not in ALLOWED:
        for child in node.children:  # unknown wrapper (<picture>, <ins class=ad>...): keep contents
            _write(child, out, base_url, embed_images)
        return

    attrs = _attrs(node, tag, base_url, embed_images)
    if attrs is None:
        return  # an <img> we are not keeping
    if tag == "a" and not any(name == "href" for name, _ in attrs):
        for child in node.children:  # a link to nowhere usable is just its words
            _write(child, out, base_url, embed_images)
        return
    rendered = "".join(f' {name}="{_attr(value)}"' for name, value in attrs)
    if tag in VOID_OUT:
        out.append(f"<{tag}{rendered}/>")
        return
    if node.tag == "summary":
        out.append(f"<p{rendered}><strong>")
        for child in node.children:
            _write(child, out, base_url, embed_images)
        out.append("</strong></p>")
        return
    out.append(f"<{tag}{rendered}>")
    for child in node.children:
        _write(child, out, base_url, embed_images)
    out.append(f"</{tag}>")


def _attrs(node: Node, tag: str, base_url: str, embed_images: bool):
    attrs = []
    if tag == "img":
        url = image_src(node, base_url)
        if not url or not embed_images:
            return None
        attrs.append(("src", f"images/{image_token(url)}"))
        attrs.append(("alt", squash(node.get("alt"))))
        return attrs
    if tag == "a":
        href = node.get("href").strip()
        parsed = split(href)
        if parsed is None:
            pass
        elif parsed.scheme in ("http", "https") and parsed.hostname:
            attrs.append(("href", href))
        elif href and not parsed.scheme and not href.startswith("#"):
            absolute = site.absolute(base_url, href)
            if absolute:
                attrs.append(("href", absolute))
    if tag in ("td", "th"):
        for name in ("colspan", "rowspan"):
            if node.get(name).isdigit():
                attrs.append((name, node.get(name)))
    if tag == "ol" and node.get("start").lstrip("-").isdigit():
        attrs.append(("start", node.get("start")))
    if node.get("dir") in ("ltr", "rtl", "auto"):
        attrs.append(("dir", node.get("dir")))
    lang = node.get("lang")
    if re.fullmatch(r"[A-Za-z]{2,3}(?:-[A-Za-z0-9]{1,8})*", lang or ""):
        attrs.append(("lang", lang))
        attrs.append(("xml:lang", lang))
    if node.get("title") and tag in ("abbr", "a", "span"):
        attrs.append(("title", squash(node.get("title"))))
    style = _style(node)
    if node.tag == "center":
        style = "text-align: center" + (f"; {style}" if style else "")
    if style:
        attrs.append(("style", style))
    return attrs


def _style(node: Node) -> str:
    kept = []
    for declaration in (node.get("style") or "").split(";"):
        name, sep, value = declaration.partition(":")
        name, value = name.strip().lower(), value.strip()
        if sep and name in STYLE_PROPS and _STYLE_VALUE.match(value):
            kept.append(f"{name}: {value}")
    align = node.get("align").lower()
    if align in ("left", "right", "center", "justify") and not any(k.startswith("text-align") for k in kept):
        kept.append(f"text-align: {align}")
    return "; ".join(kept)


def _text(value: str) -> str:
    return html_module.escape(_XML_INVALID.sub("", value), quote=False)


def _attr(value: str) -> str:
    return html_module.escape(_XML_INVALID.sub("", value), quote=True)


def text_length(xhtml: str) -> int:
    return len(squash(html_module.unescape(re.sub(r"<[^>]+>", " ", xhtml))))


def resolve_images(body: str, available: Dict[str, str]) -> str:
    """Point placeholders at real files, and drop images whose download failed.

    `available` maps token -> filename (with extension) under `images/`.
    """
    def replace(match: re.Match) -> str:
        token = match.group(1)
        name = available.get(token)
        return match.group(0).replace(f'"images/{token}"', f'"images/{name}"') if name else ""

    return re.sub(r'<img src="images/([0-9a-f]{20})"[^>]*/>', replace, body)
