#!/usr/bin/env python3
"""A minimal, correct Bindery plugin.

It downloads nothing. It exists so the conformance suite can be tested against something
known-good, so the host has something to integration-test against, and so a plugin author
has a 400-line reference for what the protocol actually requires.

Standard library only.

    BINDERY_PLUGIN_TOKEN=dev BINDERY_PLUGIN_PORT=8080 python stub_plugin.py
"""

from __future__ import annotations

import base64
import hashlib
import hmac
import html
import io
import json
import os
import re
import sys
import threading
import time
import uuid
import zipfile
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, unquote, urlparse

VERSION = "1.0.0"
TOKEN = os.environ.get("BINDERY_PLUGIN_TOKEN") or None
PORT = int(os.environ.get("BINDERY_PLUGIN_PORT", "8080"))
ARTIFACT_TTL_SECONDS = 15 * 60

SUPPORTED = re.compile(r"^https?://stub\.invalid/works/(\d+)$", re.I)
UPDATED_AT = "2026-08-01T00:00:00Z"

MANIFEST = {
    "protocolVersion": 1,
    "name": "stub",
    "displayName": "Conformance Stub",
    "version": VERSION,
    "homepage": "https://github.com/OWNER/bindery",
    "description": "A reference plugin that fabricates a small EPUB. Downloads nothing real.",
    "priority": -100,
    "matches": [r"^https?://stub\.invalid/works/\d+$"],
    "formats": ["epub"],
    "capabilities": {
        "probe": True,
        "update": True,
        "metadata": True,
        "cover": True,
        "cancel": True,
    },
    "config": [
        {"key": "author_name", "label": "Author to report", "type": "string",
         "default": "A. Stub", "help": "Written into the fabricated book's metadata."},
        {"key": "chapters", "label": "Chapters to fabricate", "type": "int",
         "default": "3", "min": 1, "max": 50},
        {"key": "slow", "label": "Emit progress slowly", "type": "bool", "default": "false"},
        {"key": "api_key", "label": "Pretend API key", "type": "secret",
         "help": "Never used. Present so the host's secret handling has something to hold."},
    ],
    "actions": [
        {"name": "echo", "label": "Echo", "description": "Returns what you typed.",
         "input": [{"key": "text", "label": "Text", "type": "string", "required": True}],
         "output": {"kind": "text"}},
        {"name": "search", "label": "Search", "description": "Fabricates a few results.",
         "input": [{"key": "q", "label": "Query", "type": "string", "required": True}],
         "output": {"kind": "list", "itemAction": "download"}},
    ],
    "ui": {
        "mode": "fragment",
        "nav": [
            {"label": "Stub", "path": "/", "icon": "book"},
            {"label": "Stub search", "path": "/search", "icon": "search"},
        ],
    },
}

# jobId -> {"created": epoch, "artifacts": {artifactId: {...}}, "cancelled": bool}
JOBS: dict = {}
JOBS_LOCK = threading.Lock()

PNG_1PX = base64.b64decode(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="
)


# ---------------------------------------------------------------- fabrication


def build_epub(title: str, author: str, chapters: int) -> bytes:
    """A structurally valid minimal EPUB 3."""
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", zipfile.ZIP_DEFLATED) as zf:
        zf.writestr(zipfile.ZipInfo("mimetype"), "application/epub+zip", zipfile.ZIP_STORED)
        zf.writestr(
            "META-INF/container.xml",
            '<?xml version="1.0" encoding="UTF-8"?>\n'
            '<container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">'
            '<rootfiles><rootfile full-path="OEBPS/content.opf" '
            'media-type="application/oebps-package+xml"/></rootfiles></container>',
        )
        items, spine, docs = [], [], []
        for index in range(1, chapters + 1):
            name = f"chapter{index}.xhtml"
            items.append(f'<item id="c{index}" href="{name}" media-type="application/xhtml+xml"/>')
            spine.append(f'<itemref idref="c{index}"/>')
            docs.append((name, f"Chapter {index}", f"The stub plugin fabricated chapter {index}."))
        opf = (
            '<?xml version="1.0" encoding="UTF-8"?>\n'
            '<package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="pub-id">'
            '<metadata xmlns:dc="http://purl.org/dc/elements/1.1/">'
            f'<dc:identifier id="pub-id">urn:bindery:stub:{abs(hash(title)) % 10**8}</dc:identifier>'
            f"<dc:title>{html.escape(title)}</dc:title>"
            f"<dc:creator>{html.escape(author)}</dc:creator>"
            "<dc:language>en</dc:language>"
            f'<meta property="dcterms:modified">{UPDATED_AT}</meta>'
            "</metadata>"
            f'<manifest><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" '
            f'properties="nav"/>{"".join(items)}</manifest>'
            f'<spine>{"".join(spine)}</spine></package>'
        )
        zf.writestr("OEBPS/content.opf", opf)
        nav_items = "".join(f'<li><a href="{name}">{html.escape(heading)}</a></li>' for name, heading, _ in docs)
        zf.writestr(
            "OEBPS/nav.xhtml",
            '<?xml version="1.0" encoding="UTF-8"?>\n'
            '<html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">'
            "<head><title>Contents</title></head><body>"
            f'<nav epub:type="toc"><ol>{nav_items}</ol></nav></body></html>',
        )
        for name, heading, body in docs:
            zf.writestr(
                f"OEBPS/{name}",
                '<?xml version="1.0" encoding="UTF-8"?>\n'
                '<html xmlns="http://www.w3.org/1999/xhtml"><head>'
                f"<title>{html.escape(heading)}</title></head><body>"
                f"<h1>{html.escape(heading)}</h1><p>{html.escape(body)}</p></body></html>",
            )
    return buffer.getvalue()


def reap_expired() -> None:
    cutoff = time.time() - ARTIFACT_TTL_SECONDS
    with JOBS_LOCK:
        for job_id in [j for j, v in JOBS.items() if v["created"] < cutoff]:
            del JOBS[job_id]


# ---------------------------------------------------------------- UI fragments


def fragment_home(base: str, _query: dict) -> str:
    return (
        '<div class="bnd-card">'
        "<h2>Conformance stub</h2>"
        "<p class=\"bnd-muted\">This plugin fabricates a small EPUB so the rest of the system "
        "has something to carry. It ships no JavaScript and no CSS.</p>"
        f'<form hx-get="{html.escape(base)}/search" hx-target="#stub-results" class="bnd-row">'
        '<input class="bnd-input" type="text" name="q" placeholder="Search the void">'
        '<button class="bnd-btn bnd-btn-primary" type="submit">Search</button>'
        "</form>"
        '<div id="stub-results"></div>'
        f'<p><a href="{html.escape(base)}/search?q=hobbit">A prepared search</a></p>'
        "</div>"
    )


def fragment_search(base: str, query: dict) -> str:
    term = (query.get("q") or [""])[0]
    if not term:
        return (
            '<div class="bnd-card"><p class="bnd-muted">Type something to search for.</p>'
            f'<form hx-get="{html.escape(base)}/search" hx-target="#stub-results" class="bnd-row">'
            '<input class="bnd-input" type="text" name="q" placeholder="Search the void">'
            '<button class="bnd-btn bnd-btn-primary" type="submit">Search</button></form></div>'
        )
    rows = "".join(
        f'<li class="bnd-row"><span>{html.escape(term)} #{index}</span>'
        f'<a class="bnd-btn" href="https://stub.invalid/works/{index}">Open</a></li>'
        for index in range(1, 4)
    )
    return (
        f'<div class="bnd-card"><h3>Results for {html.escape(term)}</h3>'
        f'<ul class="bnd-list">{rows}</ul>'
        f'<p><a href="{html.escape(base)}/">Back</a></p></div>'
    )


UI_ROUTES = {
    "/": fragment_home,
    "": fragment_home,
    "/search": fragment_search,
}


# ---------------------------------------------------------------- HTTP


class Handler(BaseHTTPRequestHandler):
    server_version = f"BinderyStub/{VERSION}"
    protocol_version = "HTTP/1.0"

    # -- plumbing

    def log_message(self, fmt, *args):  # noqa: A003
        sys.stderr.write(f"[stub] {self.address_string()} {fmt % args}\n")

    def _send(self, status: int, body: bytes = b"", content_type: str = "application/json",
              extra: dict | None = None) -> None:
        self.send_response(status)
        if body or content_type:
            self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        for key, value in (extra or {}).items():
            self.send_header(key, value)
        self.end_headers()
        if body and self.command != "HEAD":
            self.wfile.write(body)

    def _json(self, status: int, payload) -> None:
        self._send(status, json.dumps(payload).encode("utf-8"), "application/json")

    def _error(self, status: int, code: str, message: str, retryable: bool = False) -> None:
        self._json(status, {"error": {"code": code, "message": message, "retryable": retryable}})

    def _body(self):
        length = int(self.headers.get("Content-Length") or 0)
        if length <= 0:
            return {}
        try:
            return json.loads(self.rfile.read(length).decode("utf-8"))
        except Exception:
            return None

    def _authorized(self) -> bool:
        if not TOKEN:
            return True
        header = self.headers.get("Authorization") or ""
        prefix = "Bearer "
        if not header.startswith(prefix):
            return False
        return hmac.compare_digest(header[len(prefix):], TOKEN)

    def _guard(self, path: str) -> bool:
        """Everything under /bindery/v1 needs the token. /healthz never does."""
        if not path.startswith("/bindery/v1"):
            return True
        if self._authorized():
            return True
        self._error(401, "auth_required", "missing or invalid bearer token")
        return False

    # -- routing

    def do_GET(self):  # noqa: N802
        parsed = urlparse(self.path)
        path, query = parsed.path, parse_qs(parsed.query)
        if not self._guard(path):
            return
        if path == "/healthz":
            return self._json(200, {"status": "ok", "version": VERSION})
        if path == "/bindery/v1/manifest":
            return self._json(200, MANIFEST)
        if path.startswith("/bindery/v1/artifacts/"):
            return self._artifact(path)
        if path.startswith("/bindery/v1/ui"):
            return self._ui(path, query)
        return self._error(404, "not_found", f"no route for GET {path}")

    def do_HEAD(self):  # noqa: N802
        self.do_GET()

    def do_POST(self):  # noqa: N802
        parsed = urlparse(self.path)
        path = parsed.path
        if not self._guard(path):
            return
        if path == "/bindery/v1/probe":
            return self._probe()
        if path == "/bindery/v1/download":
            return self._download()
        if path.startswith("/bindery/v1/actions/"):
            return self._action(unquote(path[len("/bindery/v1/actions/"):]))
        if path.startswith("/bindery/v1/ui"):
            return self._ui(path, parse_qs(parsed.query))
        return self._error(404, "not_found", f"no route for POST {path}")

    def do_DELETE(self):  # noqa: N802
        path = urlparse(self.path).path
        if not self._guard(path):
            return
        if path.startswith("/bindery/v1/jobs/"):
            job_id = path[len("/bindery/v1/jobs/"):]
            with JOBS_LOCK:
                job = JOBS.pop(job_id, None)
            if job is not None:
                job["cancelled"] = True
            return self._send(204, b"", "")
        return self._error(404, "not_found", f"no route for DELETE {path}")

    # -- endpoints

    def _probe(self):
        body = self._body()
        if body is None:
            return self._error(400, "internal", "request body is not valid JSON")
        url = body.get("url")
        if not isinstance(url, str) or not url:
            return self._error(422, "internal", "'url' is required")
        match = SUPPORTED.match(url)
        return self._json(200, {
            "supported": bool(match),
            "confidence": 1.0 if match else 0.0,
            "reason": f"stub work {match.group(1)}" if match else "not a stub.invalid work URL",
        })

    def _download(self):
        body = self._body()
        if body is None:
            return self._error(400, "internal", "request body is not valid JSON")
        url = body.get("url")
        if not isinstance(url, str) or not url:
            return self._error(422, "internal", "'url' is required")
        job_id = body.get("jobId") or str(uuid.uuid4())
        config = body.get("config") or {}
        options = body.get("options") or {}

        self.send_response(200)
        self.send_header("Content-Type", "application/x-ndjson")
        self.send_header("Cache-Control", "no-store")
        self.end_headers()

        def emit(event: dict) -> bool:
            try:
                self.wfile.write((json.dumps(event) + "\n").encode("utf-8"))
                self.wfile.flush()
                return True
            except (BrokenPipeError, ConnectionResetError, OSError):
                return False  # the host hung up: cancellation

        match = SUPPORTED.match(url)
        if not match:
            emit({"event": "result", "status": "error", "error": {
                "code": "unsupported_url",
                "message": f"stub only handles https://stub.invalid/works/<id>, got {url}",
                "retryable": False}})
            return

        work_id = match.group(1)
        if options.get("update") and options.get("knownUpdated") == UPDATED_AT:
            emit({"event": "log", "level": "info", "message": "nothing new since knownUpdated"})
            emit({"event": "result", "status": "unchanged"})
            return

        chapters = _int(config.get("chapters"), 3, 1, 50)
        author = (config.get("author_name") or "A. Stub").strip() or "A. Stub"
        slow = str(config.get("slow", "")).lower() == "true"
        title = f"Stub Work {work_id}"

        if not emit({"event": "log", "level": "info", "message": f"fabricating {chapters} chapters"}):
            return
        for index in range(1, chapters + 1):
            if slow:
                time.sleep(0.2)
            if not emit({"event": "progress",
                         "percent": round(index * 90 / chapters, 1),
                         "message": f"chapter {index}/{chapters}"}):
                return  # disconnected: stop working, keep nothing

        epub = build_epub(title, author, chapters)
        artifacts = {
            "book": {"filename": f"{title}.epub", "format": "epub",
                     "contentType": "application/epub+zip", "kind": "book",
                     "primary": True, "data": epub},
            "cover": {"filename": "cover.png", "format": "png",
                      "contentType": "image/png", "kind": "cover",
                      "primary": False, "data": PNG_1PX},
        }
        reap_expired()
        with JOBS_LOCK:
            JOBS[job_id] = {"created": time.time(), "artifacts": artifacts, "cancelled": False}

        emit({"event": "progress", "percent": 100, "message": "packaging"})
        emit({"event": "result", "status": "ok",
              "artifacts": [
                  {"id": art_id, "filename": art["filename"], "format": art["format"],
                   "contentType": art["contentType"], "kind": art["kind"],
                   "primary": art["primary"], "bytes": len(art["data"]),
                   "sha256": hashlib.sha256(art["data"]).hexdigest()}
                  for art_id, art in artifacts.items()],
              "metadata": {
                  "title": title,
                  "authors": [author],
                  "series": "Stub Series",
                  "seriesIndex": int(work_id) if work_id.isdigit() else 1,
                  "summary": "Fabricated by the Bindery conformance stub. Contains no actual story.",
                  "language": "en",
                  "tags": ["Stub", "Test Fixture"],
                  "sourceId": f"stub:{work_id}",
                  "sourceUrl": url,
                  "publisher": "Bindery",
                  "chapters": chapters,
                  "published": "2026-01-01T00:00:00Z",
                  "updated": UPDATED_AT,
              }})

    def _artifact(self, path: str):
        rest = path[len("/bindery/v1/artifacts/"):]
        parts = rest.split("/")
        if len(parts) != 2 or not parts[0] or not parts[1]:
            return self._error(404, "not_found", "expected /artifacts/{jobId}/{artifactId}")
        job_id, artifact_id = parts
        if not re.fullmatch(r"[A-Za-z0-9._-]{1,64}", artifact_id) or artifact_id in (".", ".."):
            return self._error(404, "not_found", "unknown artifact")
        reap_expired()
        with JOBS_LOCK:
            job = JOBS.get(job_id)
            artifact = job["artifacts"].get(artifact_id) if job else None
        if artifact is None:
            return self._error(404, "not_found", "unknown job or artifact")
        self._send(200, artifact["data"], artifact["contentType"], extra={
            "Content-Disposition": f'attachment; filename="{artifact["filename"]}"'
        })

    def _action(self, name: str):
        body = self._body()
        if body is None:
            return self._error(400, "internal", "request body is not valid JSON")
        payload = body.get("input") or {}
        if name == "echo":
            text = payload.get("text")
            if not isinstance(text, str) or not text:
                return self._json(200, {"status": "error", "error": {
                    "code": "internal", "message": "'text' is required", "retryable": False}})
            return self._json(200, {"status": "ok", "output": {"kind": "text", "text": text}})
        if name == "search":
            term = (payload.get("q") or "").strip()
            if not term:
                return self._json(200, {"status": "error", "error": {
                    "code": "internal", "message": "'q' is required", "retryable": False}})
            return self._json(200, {"status": "ok", "output": {
                "kind": "list",
                "items": [{"title": f"{term} #{i}",
                           "subtitle": "fabricated by the stub",
                           "url": f"https://stub.invalid/works/{i}"} for i in range(1, 4)]}})
        return self._error(404, "not_found", f"no action named {name!r}")

    def _ui(self, path: str, query: dict):
        base = self.headers.get("X-Bindery-Base") or "/plugins/stub/ui"
        route = path[len("/bindery/v1/ui"):] or "/"
        renderer = UI_ROUTES.get(route)
        if renderer is None:
            return self._send(404, b'<div class="bnd-card"><p>No such page.</p></div>', "text/html")
        fragment = renderer(base.rstrip("/"), query)
        self._send(200, fragment.encode("utf-8"), "text/html",
                   extra={"Cache-Control": "no-store"})


def _int(value, default: int, low: int, high: int) -> int:
    try:
        parsed = int(str(value))
    except (TypeError, ValueError):
        return default
    return max(low, min(high, parsed))


def main() -> int:
    if not TOKEN:
        sys.stderr.write("[stub] WARNING: BINDERY_PLUGIN_TOKEN is unset; accepting all requests\n")
    server = ThreadingHTTPServer(("0.0.0.0", PORT), Handler)
    server.daemon_threads = True
    sys.stderr.write(f"[stub] listening on :{PORT}\n")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
