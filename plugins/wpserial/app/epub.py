"""Writing the book: a small, valid EPUB 3 with an EPUB 2 NCX for older readers.

Standard library only — `zipfile` and string templates — for the same reason Hedgerow's
writer is: "list the chapters, zip a handful of files" does not justify an EPUB library.
Every chapter document is checked with an XML parser before it goes in; one that fails
(which the allowlist serializer should make impossible) is replaced with its plain text
rather than shipping a book an ereader refuses to open.
"""

from __future__ import annotations

import html as html_module
import re
import uuid
import zipfile
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Dict, List, Optional, Tuple
from xml.etree import ElementTree

XHTML = "http://www.w3.org/1999/xhtml"

STYLESHEET = """\
body { margin: 0 4%; line-height: 1.45; }
h2.chapter-title { margin: 1.5em 0 1em; text-align: center; }
p { margin: 0 0 0.8em; }
img { max-width: 100%; height: auto; }
blockquote { margin: 1em 1.5em; }
table { border-collapse: collapse; }
td, th { border: 1px solid #888; padding: 0.2em 0.4em; }
.cover { text-align: center; margin: 0; padding: 0; }
.cover img { max-height: 100%; }
"""


@dataclass
class Chapter:
    title: str
    body: str


def build(out_path: str, *, title: str, authors: List[str], language: str, identifier: str,
          chapters: List[Chapter], images: Dict[str, Tuple[bytes, str]],
          cover: Optional[Tuple[bytes, str]] = None, description: Optional[str] = None,
          source_url: Optional[str] = None, publisher: Optional[str] = None,
          published: Optional[str] = None, modified: Optional[str] = None) -> int:
    """Write the EPUB. Returns how many chapter bodies had to fall back to plain text."""
    language = language if re.fullmatch(r"[A-Za-z]{2,3}(?:-[A-Za-z0-9]{1,8})*", language or "") else "en"
    modified = modified or datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    manifest: List[str] = []
    spine: List[str] = []
    files: List[Tuple[str, bytes]] = []
    fallbacks = 0

    manifest.append('<item id="css" href="style.css" media-type="text/css"/>')
    files.append(("OEBPS/style.css", STYLESHEET.encode("utf-8")))

    cover_meta = ""
    if cover:
        data, media_type = cover
        ext = {"image/jpeg": "jpg", "image/png": "png", "image/gif": "gif", "image/webp": "webp"}.get(media_type, "jpg")
        manifest.append(f'<item id="cover-image" href="cover.{ext}" media-type="{media_type}" properties="cover-image"/>')
        manifest.append('<item id="cover" href="cover.xhtml" media-type="application/xhtml+xml"/>')
        spine.append('<itemref idref="cover" linear="yes"/>')
        files.append((f"OEBPS/cover.{ext}", data))
        files.append(("OEBPS/cover.xhtml", _document(title, language,
                                                      f'<div class="cover"><img src="cover.{ext}" alt="{_esc(title)}"/></div>').encode("utf-8")))
        cover_meta = '\n    <meta name="cover" content="cover-image"/>'

    for index, (name, (data, media_type)) in enumerate(sorted(images.items()), start=1):
        manifest.append(f'<item id="img{index}" href="images/{_esc(name)}" media-type="{media_type}"/>')
        files.append((f"OEBPS/images/{name}", data))

    nav_items, ncx_points = [], []
    for index, chapter in enumerate(chapters, start=1):
        name = f"chap{index:04d}.xhtml"
        body = f'<h2 class="chapter-title">{_esc(chapter.title)}</h2>\n{chapter.body}'
        document = _document(chapter.title, language, body)
        if not _well_formed(document):
            fallbacks += 1
            document = _document(chapter.title, language,
                                 f'<h2 class="chapter-title">{_esc(chapter.title)}</h2>\n' + _plain(chapter.body))
        files.append((f"OEBPS/{name}", document.encode("utf-8")))
        manifest.append(f'<item id="c{index}" href="{name}" media-type="application/xhtml+xml"/>')
        spine.append(f'<itemref idref="c{index}"/>')
        nav_items.append(f'<li><a href="{name}">{_esc(chapter.title)}</a></li>')
        ncx_points.append(
            f'<navPoint id="n{index}" playOrder="{index}"><navLabel><text>{_esc(chapter.title)}</text>'
            f'</navLabel><content src="{name}"/></navPoint>')

    manifest.insert(0, '<item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>')
    manifest.insert(1, '<item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/>')

    metadata = [
        f'<dc:identifier id="pub-id">{_esc(identifier)}</dc:identifier>',
        f"<dc:title>{_esc(title)}</dc:title>",
        f"<dc:language>{_esc(language)}</dc:language>",
        f'<meta property="dcterms:modified">{_esc(modified)}</meta>',
    ]
    metadata += [f"<dc:creator>{_esc(author)}</dc:creator>" for author in authors if author]
    if description:
        metadata.append(f"<dc:description>{_esc(description)}</dc:description>")
    if source_url:
        # Hedgerow (and anything else) can tell where this book came from without guessing.
        metadata.append(f"<dc:source>{_esc(source_url)}</dc:source>")
    if publisher:
        metadata.append(f"<dc:publisher>{_esc(publisher)}</dc:publisher>")
    if published:
        metadata.append(f"<dc:date>{_esc(published)}</dc:date>")

    opf = (
        '<?xml version="1.0" encoding="utf-8"?>\n'
        '<package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="pub-id">\n'
        '  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">\n    '
        + "\n    ".join(metadata) + cover_meta + "\n  </metadata>\n"
        "  <manifest>\n    " + "\n    ".join(manifest) + "\n  </manifest>\n"
        '  <spine toc="ncx">\n    ' + "\n    ".join(spine) + "\n  </spine>\n"
        "</package>\n"
    )
    nav = (
        '<?xml version="1.0" encoding="utf-8"?>\n'
        f'<!DOCTYPE html>\n<html xmlns="{XHTML}" xmlns:epub="http://www.idpf.org/2007/ops" '
        f'lang="{language}" xml:lang="{language}">\n'
        f"<head><meta charset=\"utf-8\"/><title>{_esc(title)}</title></head>\n"
        '<body><nav epub:type="toc" id="toc"><h1>Contents</h1>\n<ol>\n'
        + "\n".join(nav_items) + "\n</ol></nav></body></html>\n"
    )
    ncx = (
        '<?xml version="1.0" encoding="utf-8"?>\n'
        '<ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1">\n'
        f'<head><meta name="dtb:uid" content="{_esc(identifier)}"/></head>\n'
        f"<docTitle><text>{_esc(title)}</text></docTitle>\n<navMap>\n"
        + "\n".join(ncx_points) + "\n</navMap>\n</ncx>\n"
    )

    with zipfile.ZipFile(out_path, "w", zipfile.ZIP_DEFLATED) as book:
        # mimetype first and stored: the one packaging rule every reader checks.
        book.writestr(zipfile.ZipInfo("mimetype"), "application/epub+zip", compress_type=zipfile.ZIP_STORED)
        book.writestr("META-INF/container.xml", _CONTAINER)
        book.writestr("OEBPS/content.opf", opf)
        book.writestr("OEBPS/nav.xhtml", nav)
        book.writestr("OEBPS/toc.ncx", ncx)
        for name, data in files:
            book.writestr(name, data)
    return fallbacks


def stable_uuid(source_id: str) -> str:
    return "urn:uuid:" + str(uuid.uuid5(uuid.NAMESPACE_URL, source_id))


def _document(title: str, language: str, body: str) -> str:
    return (
        '<?xml version="1.0" encoding="utf-8"?>\n'
        f'<!DOCTYPE html>\n<html xmlns="{XHTML}" lang="{language}" xml:lang="{language}">\n'
        f'<head><meta charset="utf-8"/><title>{_esc(title)}</title>'
        '<link rel="stylesheet" type="text/css" href="style.css"/></head>\n'
        f"<body>\n{body}\n</body></html>\n"
    )


def _well_formed(document: str) -> bool:
    try:
        ElementTree.fromstring(re.sub(r"<!DOCTYPE[^>]*>", "", document).encode("utf-8"))
        return True
    except ElementTree.ParseError:
        return False


def _plain(body: str) -> str:
    text = html_module.unescape(re.sub(r"<(?:br|/p|/div|/li|/h\d)[^>]*>", "\n", body))
    text = re.sub(r"<[^>]+>", "", text)
    paragraphs = [p.strip() for p in re.split(r"\n\s*\n|\n", text) if p.strip()]
    return "\n".join(f"<p>{_esc(p)}</p>" for p in paragraphs)


def _esc(value) -> str:
    return html_module.escape(str(value if value is not None else ""), quote=True)


_CONTAINER = (
    '<?xml version="1.0" encoding="utf-8"?>\n'
    '<container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">\n'
    '  <rootfiles>\n'
    '    <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>\n'
    "  </rootfiles>\n</container>\n"
)
