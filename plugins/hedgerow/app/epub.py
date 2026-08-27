"""Reading chapters out of EPUBs and writing one back.

Both directions are pure stdlib — `zipfile` plus a little XML — because pulling in a full
EPUB library for what amounts to "list the spine, read each document, then zip a handful of
files" is more dependency than the job is worth. Two things use this: ingesting a Discord
advance-copy EPUB into individual chapters, and emitting the merged book at the end.
"""

from __future__ import annotations

import html as html_module
import io
import logging
import os
import re
import zipfile
from dataclasses import dataclass
from typing import List, Optional
from xml.etree import ElementTree

log = logging.getLogger("bindery.hedgerow")

XHTML = "http://www.w3.org/1999/xhtml"
CONTAINER = "META-INF/container.xml"

# Spine documents whose only job is front matter, not story text. Skipped on ingest so a
# cover page or table of contents never becomes a "chapter".
FRONT_MATTER = re.compile(r"(cover|title|toc|nav|contents|copyright|colophon)", re.I)

_TAG = re.compile(r"<[^>]+>")
_BODY = re.compile(r"<body[^>]*>(.*)</body>", re.I | re.S)
_TITLE_TAG = re.compile(r"<title[^>]*>(.*?)</title>", re.I | re.S)
_HEADING = re.compile(r"<h[1-3][^>]*>(.*?)</h[1-3]>", re.I | re.S)


@dataclass
class ParsedChapter:
    title: str
    body_html: str


def read_chapters(epub_path: str) -> List[ParsedChapter]:
    """Every spine document that reads like a chapter, in spine order."""
    chapters: List[ParsedChapter] = []
    try:
        with zipfile.ZipFile(epub_path) as book:
            names = set(book.namelist())
            opf_path = _opf_path(book, names)
            if not opf_path:
                return []
            base = os.path.dirname(opf_path)
            for href in _spine_hrefs(book, opf_path):
                full = _resolve(base, href)
                if full not in names or FRONT_MATTER.search(os.path.basename(full)):
                    continue
                raw = book.read(full).decode("utf-8", "replace")
                body = _extract_body(raw)
                if not _TAG.sub("", body).strip():
                    continue  # nothing but markup: not a chapter
                chapters.append(ParsedChapter(title=_extract_title(raw, full), body_html=body))
    except (zipfile.BadZipFile, KeyError, ElementTree.ParseError, OSError) as exc:
        log.warning("could not read chapters from %s: %s", epub_path, exc)
    return chapters


def build_epub(out_path: str, *, title: str, author: str, chapters: List[ParsedChapter],
               source_id: Optional[str] = None) -> None:
    """Write a minimal but valid EPUB 3 with one XHTML document per chapter."""
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    manifest_items, spine_items, nav_items = [], [], []
    docs = []
    for index, chapter in enumerate(chapters, start=1):
        item_id = f"ch{index}"
        filename = f"chap{index:04d}.xhtml"
        docs.append((filename, _chapter_document(chapter)))
        manifest_items.append(
            f'<item id="{item_id}" href="{filename}" media-type="application/xhtml+xml"/>'
        )
        spine_items.append(f'<itemref idref="{item_id}"/>')
        nav_items.append(f'<li><a href="{filename}">{_esc(chapter.title)}</a></li>')

    opf = _opf(title, author, manifest_items, spine_items, source_id)
    nav = _nav(title, nav_items)

    # Store the mimetype first and uncompressed — the one EPUB packaging rule that matters.
    with zipfile.ZipFile(out_path, "w", zipfile.ZIP_DEFLATED) as book:
        book.writestr(
            zipfile.ZipInfo("mimetype"), "application/epub+zip", compress_type=zipfile.ZIP_STORED
        )
        book.writestr(CONTAINER, _CONTAINER_XML)
        book.writestr("OEBPS/content.opf", opf)
        book.writestr("OEBPS/nav.xhtml", nav)
        for filename, document in docs:
            book.writestr(f"OEBPS/{filename}", document)


# ---------------------------------------------------------------- reading internals


def _opf_path(book: zipfile.ZipFile, names) -> Optional[str]:
    if CONTAINER not in names:
        return next((n for n in names if n.lower().endswith(".opf")), None)
    root = ElementTree.parse(io.BytesIO(book.read(CONTAINER))).getroot()
    for element in root.iter():
        if element.tag.endswith("rootfile"):
            return element.get("full-path")
    return None


def _spine_hrefs(book: zipfile.ZipFile, opf_path: str) -> List[str]:
    root = ElementTree.parse(io.BytesIO(book.read(opf_path))).getroot()
    items, spine = {}, []
    for element in root.iter():
        tag = element.tag.rsplit("}", 1)[-1]
        if tag == "item":
            items[element.get("id")] = element.get("href")
        elif tag == "itemref":
            spine.append(element.get("idref"))
    return [items[idref] for idref in spine if idref in items]


def _resolve(base: str, href: str) -> str:
    joined = os.path.normpath(os.path.join(base, href)) if base else os.path.normpath(href)
    return joined.replace(os.sep, "/")


def _extract_body(raw: str) -> str:
    match = _BODY.search(raw)
    return (match.group(1) if match else raw).strip()


def _extract_title(raw: str, href: str) -> str:
    for pattern in (_TITLE_TAG, _HEADING):
        match = pattern.search(raw)
        if match:
            text = html_module.unescape(_TAG.sub("", match.group(1))).strip()
            if text:
                return text
    return os.path.splitext(os.path.basename(href))[0]


# ---------------------------------------------------------------- writing internals


def _chapter_document(chapter: ParsedChapter) -> str:
    return (
        '<?xml version="1.0" encoding="utf-8"?>\n'
        f'<html xmlns="{XHTML}"><head><meta charset="utf-8"/>'
        f"<title>{_esc(chapter.title)}</title></head>"
        f"<body><h2>{_esc(chapter.title)}</h2>{chapter.body_html}</body></html>"
    )


def _opf(title: str, author: str, manifest_items, spine_items, source_id: Optional[str]) -> str:
    identifier = _esc(source_id or f"hedgerow:{abs(hash(title))}")
    manifest = "\n    ".join(
        ['<item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>']
        + manifest_items
    )
    spine = "\n    ".join(spine_items)
    return (
        '<?xml version="1.0" encoding="utf-8"?>\n'
        '<package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="pub-id">\n'
        '  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">\n'
        f'    <dc:identifier id="pub-id">{identifier}</dc:identifier>\n'
        f"    <dc:title>{_esc(title)}</dc:title>\n"
        f"    <dc:creator>{_esc(author)}</dc:creator>\n"
        "    <dc:language>en</dc:language>\n"
        "  </metadata>\n"
        f"  <manifest>\n    {manifest}\n  </manifest>\n"
        f"  <spine>\n    {spine}\n  </spine>\n"
        "</package>\n"
    )


def _nav(title: str, nav_items) -> str:
    items = "\n      ".join(nav_items)
    return (
        '<?xml version="1.0" encoding="utf-8"?>\n'
        f'<html xmlns="{XHTML}" xmlns:epub="http://www.idpf.org/2007/ops">\n'
        f"<head><meta charset=\"utf-8\"/><title>{_esc(title)}</title></head>\n"
        '<body><nav epub:type="toc"><h1>Contents</h1>\n'
        f"    <ol>\n      {items}\n    </ol>\n  </nav></body></html>"
    )


def _esc(value) -> str:
    return html_module.escape(str(value if value is not None else ""), quote=True)


COVER_NAME_RE = re.compile(r"(^|/)cover[^/]*\.(jpe?g|png|gif|webp)$", re.I)
IMAGE_TYPES = {
    ".jpg": "image/jpeg", ".jpeg": "image/jpeg", ".png": "image/png",
    ".gif": "image/gif", ".webp": "image/webp",
}


def extract_cover(epub_path: str):
    """(filename, content_type, bytes) for the EPUB's cover, or None.

    Reads the OPF's declared cover first and only then falls back to a filename match, the
    same order the FanFicFare plugin uses — a cover that is right by declaration beats one
    guessed from a name.
    """
    try:
        with zipfile.ZipFile(epub_path) as book:
            names = book.namelist()
            opf_path = _opf_path(book, set(names))
            if opf_path:
                href = _cover_href(book, opf_path)
                if href:
                    full = _resolve(os.path.dirname(opf_path), href)
                    if full in names:
                        return _as_cover(full, book.read(full))
            for name in names:
                if COVER_NAME_RE.search(name):
                    return _as_cover(name, book.read(name))
    except (zipfile.BadZipFile, KeyError, ElementTree.ParseError, OSError) as exc:
        log.warning("cover extraction failed for %s: %s", epub_path, exc)
    return None


def _as_cover(name: str, data: bytes):
    ext = os.path.splitext(name)[1].lower()
    return f"cover{ext or '.jpg'}", IMAGE_TYPES.get(ext, "application/octet-stream"), data


def _cover_href(book: zipfile.ZipFile, opf_path: str):
    root = ElementTree.parse(io.BytesIO(book.read(opf_path))).getroot()
    items, cover_id = {}, None
    for element in root.iter():
        tag = element.tag.rsplit("}", 1)[-1]
        if tag == "meta" and (element.get("name") or "").lower() == "cover":
            cover_id = element.get("content")
        elif tag == "item":
            items[element.get("id")] = element
    if cover_id and cover_id in items:
        return items[cover_id].get("href")
    for element in items.values():
        if "cover-image" in (element.get("properties") or ""):
            return element.get("href")
    return None


_CONTAINER_XML = (
    '<?xml version="1.0" encoding="utf-8"?>\n'
    '<container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">\n'
    '  <rootfiles>\n'
    '    <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>\n'
    "  </rootfiles>\n</container>\n"
)
