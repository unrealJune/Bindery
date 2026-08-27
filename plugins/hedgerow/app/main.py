"""Hedgerow's HTTP surface: the full plugin protocol plus a sandboxed UI.

The protocol endpoints are the same six every plugin implements. What is unusual here is the
*download*: it does not fetch one thing, it reconciles two. A Royal Road scrape and whatever
the Discord bot has already ingested for the same work are merged, ordered, and emitted as a
single EPUB. The bot, the store, and the merge logic each live in their own module; this file
is the plumbing that connects them to HTTP.
"""

from __future__ import annotations

import asyncio
import contextlib
import hmac
import json
import logging
import os
import time
from typing import Any, AsyncIterator, Dict, List, Optional

from fastapi import Depends, FastAPI, HTTPException, Request, Response
from fastapi.exceptions import RequestValidationError
from fastapi.responses import FileResponse, JSONResponse, StreamingResponse
from pydantic import BaseModel, Field

from . import epub, merge, royalroad, ui
from .discord_bot import BotManager, _split_channels
from .jobs import JobStore
from .manifest import VERSION, build_manifest
from .store import SOURCE_ROYALROAD, Store

logging.basicConfig(
    level=os.environ.get("BINDERY_LOG_LEVEL", "INFO").upper(),
    format="%(asctime)s %(levelname)s %(name)s %(message)s",
)
log = logging.getLogger("bindery.hedgerow")

TOKEN = os.environ.get("BINDERY_PLUGIN_TOKEN") or None
META_TIMEOUT = float(os.environ.get("BINDERY_RR_META_TIMEOUT", "120"))
DOWNLOAD_TIMEOUT = float(os.environ.get("BINDERY_RR_DOWNLOAD_TIMEOUT", "600"))
ACTION_TIMEOUT = float(os.environ.get("BINDERY_RR_ACTION_TIMEOUT", "28"))

STATIC_DIR = os.path.join(os.path.dirname(__file__), "static")

store = Store()
jobs = JobStore()
bot = BotManager(store)


# ---------------------------------------------------------------- app


@contextlib.asynccontextmanager
async def lifespan(app: FastAPI):
    if not TOKEN:
        log.warning("BINDERY_PLUGIN_TOKEN is unset: accepting unauthenticated requests. "
                    "This is for local development only.")
    jobs.sweep_orphans()  # per-job scratch only; durable data/ is never swept
    await bot.start_from_persisted()  # revives a previously configured bot; a no-op without one
    reaper = asyncio.create_task(jobs.reap_forever())
    try:
        yield
    finally:
        reaper.cancel()
        with contextlib.suppress(asyncio.CancelledError):
            await reaper
        await bot.stop()


app = FastAPI(
    title="Bindery plugin: Hedgerow",
    version=VERSION,
    lifespan=lifespan,
    docs_url=None,
    redoc_url=None,
    openapi_url=None,
)


def error_body(code: str, message: str, retryable: bool = False) -> dict:
    return {"error": {"code": code, "message": message, "retryable": retryable}}


@app.exception_handler(RequestValidationError)
async def on_validation_error(_: Request, exc: RequestValidationError) -> JSONResponse:
    detail = "; ".join(
        f"{'.'.join(str(part) for part in err.get('loc', [])[1:])}: {err.get('msg')}"
        for err in exc.errors()
    )
    return JSONResponse(status_code=422, content=error_body("internal", detail or "invalid request body"))


@app.exception_handler(HTTPException)
async def on_http_error(_: Request, exc: HTTPException) -> Response:
    if exc.status_code == 204:
        return Response(status_code=204)
    code = {401: "auth_required", 404: "not_found", 429: "rate_limited"}.get(exc.status_code, "internal")
    return JSONResponse(status_code=exc.status_code, content=error_body(code, str(exc.detail)))


async def require_token(request: Request) -> None:
    """Bearer check on /bindery/v1 only. /healthz stays open for liveness probes."""
    if not TOKEN:
        return
    header = request.headers.get("authorization", "")
    prefix = "Bearer "
    if not header.startswith(prefix) or not hmac.compare_digest(header[len(prefix):], TOKEN):
        raise HTTPException(status_code=401, detail="missing or invalid bearer token")


# ---------------------------------------------------------------- models


class ProbeRequest(BaseModel):
    url: str = Field(min_length=1)


class DownloadOptions(BaseModel):
    update: bool = False
    knownSourceId: Optional[str] = None
    knownUpdated: Optional[str] = None
    formats: List[str] = Field(default_factory=list)


class DownloadRequest(BaseModel):
    jobId: str = Field(min_length=1)
    url: str = Field(min_length=1)
    config: Dict[str, Any] = Field(default_factory=dict)
    options: DownloadOptions = Field(default_factory=DownloadOptions)


class ActionRequest(BaseModel):
    action: Optional[str] = None
    input: Dict[str, Any] = Field(default_factory=dict)
    config: Dict[str, Any] = Field(default_factory=dict)


# ---------------------------------------------------------------- protocol endpoints


@app.get("/healthz")
async def healthz() -> dict:
    # Cheap and network-free by contract: report liveness and the bot's last-known state.
    return {"status": "ok", "version": VERSION, "discord": bot.status()["status"]}


@app.get("/bindery/v1/manifest", dependencies=[Depends(require_token)])
async def manifest() -> dict:
    return build_manifest()


@app.post("/bindery/v1/probe", dependencies=[Depends(require_token)])
async def probe(body: ProbeRequest) -> dict:
    url = body.url
    if royalroad.is_royalroad(url):
        return {"supported": True, "confidence": 1.0, "reason": "Royal Road fiction URL"}
    if royalroad.is_hedgerow(url):
        return {"supported": True, "confidence": 0.9, "reason": "hungryshedgerow.net mirror"}
    normalized = royalroad.normalize(url)
    if normalized:
        return {"supported": True, "confidence": 0.8, "reason": f"{normalized[1]} adapter"}
    return {"supported": False, "confidence": 0.0, "reason": "not a Royal Road or hungryshedgerow URL"}


@app.post("/bindery/v1/download", dependencies=[Depends(require_token)])
async def download(body: DownloadRequest, request: Request) -> StreamingResponse:
    return StreamingResponse(
        run_download(body, request),
        media_type="application/x-ndjson",
        headers={"Cache-Control": "no-store", "X-Accel-Buffering": "no"},
    )


@app.api_route("/bindery/v1/artifacts/{job_id}/{artifact_id}", methods=["GET", "HEAD"],
               dependencies=[Depends(require_token)])
async def artifact(job_id: str, artifact_id: str):
    found = await jobs.artifact(job_id, artifact_id)
    if found is None or not os.path.exists(found.path):
        raise HTTPException(status_code=404, detail="unknown job or artifact")
    return FileResponse(
        found.path,
        media_type=found.content_type,
        filename=found.filename,
        headers={"Cache-Control": "no-store"},
    )


@app.delete("/bindery/v1/jobs/{job_id}", status_code=204, dependencies=[Depends(require_token)])
async def delete_job(job_id: str) -> Response:
    await jobs.discard(job_id)  # idempotent by contract: unknown is still 204
    return Response(status_code=204)


@app.post("/bindery/v1/actions/{action}", dependencies=[Depends(require_token)])
async def run_action(action: str, body: ActionRequest) -> dict:
    await _apply_bot_config(body.config)
    if action == "pending":
        return _pending()
    if action == "rescan":
        return await _rescan(body)
    raise HTTPException(status_code=404, detail=f"no action named {action!r}")


# ---------------------------------------------------------------- the download


def _event(payload: dict) -> bytes:
    return (json.dumps(payload, ensure_ascii=False) + "\n").encode("utf-8")


async def run_download(body: DownloadRequest, request: Request) -> AsyncIterator[bytes]:
    job = await jobs.open(body.jobId)
    log.info("job %s: download %s", job.id, body.url)
    await _apply_bot_config(body.config)
    try:
        async for chunk in _download_phases(body, job, request):
            yield chunk
    except asyncio.CancelledError:
        log.info("job %s: cancelled by disconnect", job.id)
        await jobs.discard(job.id)
        raise
    except royalroad.RoyalRoadError as exc:
        log.warning("job %s: %s: %s", job.id, exc.code, exc.message)
        yield _event({"event": "result", "status": "error",
                      "error": {"code": exc.code, "message": exc.message, "retryable": exc.retryable}})
    except Exception as exc:  # never end the stream without a result
        log.exception("job %s: unhandled failure", job.id)
        yield _event({"event": "result", "status": "error",
                      "error": {"code": "internal", "message": f"{type(exc).__name__}: {exc}",
                                "retryable": True}})


async def _download_phases(body: DownloadRequest, job, request: Request) -> AsyncIterator[bytes]:
    resolved = _resolve_work(body.url)
    if resolved is None:
        yield _event({"event": "result", "status": "error", "error": {
            "code": "unsupported_url",
            "message": "neither a Royal Road nor a hungryshedgerow.net URL",
            "retryable": False}})
        return
    work, rr_url = resolved

    scrape_meta: dict = {}
    if rr_url:
        yield _event({"event": "progress", "percent": 5, "message": "reading Royal Road metadata"})
        scrape_meta, new_count, cover_source = await _fetch_and_ingest(
            work, rr_url, body.config, job, DOWNLOAD_TIMEOUT, request)
        yield _event({"event": "log", "level": "info",
                      "message": f"Royal Road supplied {new_count} new chapter(s)"})
    else:
        cover_source = None
        yield _event({"event": "log", "level": "info",
                      "message": "no Royal Road source bound; merging Discord chapters only"})

    # Reconcile the two sources into one order. Merging always runs — a Discord-only or
    # scrape-added run both end here, and 'unchanged' is deliberately not claimed because a
    # forwarded EPUB can add chapters even when the Royal Road side has not moved.
    yield _event({"event": "progress", "percent": 85, "message": "reconciling chapters"})
    work = store.get_work(work.id) or work
    ordered = merge.order_chapters(store.list_chapters(work.id))
    if not ordered:
        yield _event({"event": "result", "status": "error", "error": {
            "code": "not_found",
            "message": "no chapters have been ingested for this work yet",
            "retryable": False}})
        return

    merged_path = os.path.join(job.workdir, "out", "merged.epub")
    epub.build_epub(
        merged_path,
        title=work.title,
        author=work.author,
        chapters=[epub.ParsedChapter(title=c.title, body_html=store.chapter_body(c.id)) for c in ordered],
        source_id=work.source_id,
    )

    artifacts = [jobs.add_artifact(
        job, "book", merged_path,
        filename=_filename(work.title),
        fmt="epub",
        content_type="application/epub+zip",
        kind="book",
        primary=True,
    )]

    if cover_source and os.path.exists(cover_source):
        cover = epub.extract_cover(cover_source)
        if cover:
            cover_name, cover_type, cover_bytes = cover
            cover_path = os.path.join(job.workdir, "out", cover_name)
            with open(cover_path, "wb") as handle:
                handle.write(cover_bytes)
            artifacts.append(jobs.add_artifact(
                job, "cover", cover_path,
                filename=cover_name,
                fmt=os.path.splitext(cover_name)[1].lstrip(".").lower() or "jpg",
                content_type=cover_type,
                kind="cover",
                primary=False,
            ))

    yield _event({"event": "progress", "percent": 100, "message": "done"})
    yield _event({"event": "result", "status": "ok",
                  "artifacts": [item.describe() for item in artifacts],
                  "metadata": _metadata(work, ordered, scrape_meta)})


def _resolve_work(url: str):
    """(work, royalroad_url_or_None) for a URL, or None if we do not claim it."""
    normalized = royalroad.normalize(url)
    if royalroad.is_royalroad(url) or (normalized and "royalroad" in normalized[1]):
        rr_url = normalized[0] if normalized else url
        return store.get_or_create_work(source_url=rr_url), rr_url
    if royalroad.is_hedgerow(url):
        work = store.get_or_create_work(hedgerow_url=url)
        return work, work.source_url
    return None


async def _fetch_and_ingest(work, rr_url: str, config: dict, job, timeout: float,
                            request: Optional[Request]):
    """Scrape Royal Road once and ingest its chapters, first-seen precedence intact.

    Returns (metadata, newly_ingested_count, epub_path). The EPUB is kept so its cover can
    be lifted from it afterwards.
    """
    ini_files = _write_ini(job.workdir, config)

    meta_run = await royalroad.run(royalroad.meta_args(rr_url, ini_files), job.workdir, META_TIMEOUT)
    raw = _parse_json(meta_run.stdout)
    if raw is None:
        raise royalroad.classify(meta_run.output)
    metadata = royalroad.map_metadata(raw, rr_url)
    store.update_work(work.id, title=metadata.get("title"), author=metadata.get("author", ""),
                      source_id=metadata.get("sourceId"))

    if request is not None and await request.is_disconnected():
        raise asyncio.CancelledError()

    out_relative = os.path.join("out", "royalroad.epub")
    out_path = os.path.join(job.workdir, out_relative)
    dl = await royalroad.run(royalroad.download_args(rr_url, ini_files, out_relative), job.workdir, timeout)
    if not os.path.exists(out_path):
        raise royalroad.classify(dl.output)

    published = metadata.get("published")
    new_count = 0
    for chapter in epub.read_chapters(out_path):
        arc, part = merge.parse_number(chapter.title)
        if store.ingest_chapter(
            work_id=work.id,
            chapter_key=merge.chapter_key(chapter.title),
            source=SOURCE_ROYALROAD,
            title=chapter.title,
            body_html=chapter.body_html,
            arc=arc, part=part, published=published,
        ):
            new_count += 1
    return metadata, new_count, out_path


def _metadata(work, ordered, scrape_meta: dict) -> dict:
    metadata = {
        "title": work.title or "Untitled",
        "authors": [work.author] if work.author else [],
        "chapters": len(ordered),
        "sourceId": work.source_id or (f"hedgerow:{work.id}"),
        "sourceUrl": work.source_url or work.hedgerow_url,
        "publisher": "Royal Road",
        "language": "en",
    }
    for key in ("published", "updated"):
        if scrape_meta.get(key):
            metadata[key] = scrape_meta[key]
    return {k: v for k, v in metadata.items() if v not in (None, "", [])}


def _write_ini(workdir: str, config: Dict[str, Any]) -> List[str]:
    generated = royalroad.build_ini(config)
    if not generated.strip():
        return []
    path = os.path.join(workdir, "hedgerow.ini")
    with open(path, "w", encoding="utf-8") as handle:
        handle.write(generated)
    return [path]


def _filename(title: str) -> str:
    return f"{(title or 'serial').strip()}.epub"


def _parse_json(text: str):
    start = text.find("{")
    end = text.rfind("}")
    if start < 0 or end <= start:
        return None
    try:
        return json.loads(text[start:end + 1])
    except json.JSONDecodeError:
        return None


# ---------------------------------------------------------------- actions


def _pending() -> dict:
    items = []
    for work in store.list_works():
        count = len(store.list_chapters(work.id))
        url = work.source_url or work.hedgerow_url
        if count and url:
            items.append({
                "title": work.title or f"Work {work.id}",
                "subtitle": f"{count} chapter(s) ingested",
                "url": url,
            })
    return {"status": "ok", "output": {"kind": "list", "items": items}}


async def _rescan(body: ActionRequest) -> dict:
    url = str(body.input.get("url") or "").strip()
    if not royalroad.is_royalroad(url) and not (royalroad.normalize(url) or [None, ""])[1].startswith("royalroad"):
        return {"status": "error",
                "error": {"code": "unsupported_url", "message": "expected a Royal Road URL",
                          "retryable": False}}
    resolved = _resolve_work(url)
    if resolved is None or not resolved[1]:
        return {"status": "error",
                "error": {"code": "unsupported_url", "message": "expected a Royal Road URL",
                          "retryable": False}}
    work, rr_url = resolved
    job = await jobs.open(f"rescan-{int(time.time() * 1000)}")
    try:
        _, new_count, _ = await _fetch_and_ingest(work, rr_url, body.config, job, ACTION_TIMEOUT, None)
        total = len(store.list_chapters(work.id))
        return {"status": "ok", "output": {
            "kind": "message", "level": "info",
            "message": f"Rescanned '{work.title}': {new_count} new chapter(s), {total} total."}}
    except royalroad.RoyalRoadError as exc:
        return {"status": "error",
                "error": {"code": exc.code, "message": exc.message, "retryable": exc.retryable}}
    finally:
        await jobs.discard(job.id)


async def _apply_bot_config(config: Dict[str, Any]) -> None:
    """Let host-stored config drive the always-on bot. Absent keys leave the bot untouched."""
    if "discord_token" not in config and "discord_channels" not in config:
        return
    token = config.get("discord_token")
    channels = _split_channels(str(config.get("discord_channels") or ""))
    with contextlib.suppress(Exception):
        await bot.apply_config(token, channels)


# ---------------------------------------------------------------- UI (sandboxed)


# Explicit routes are registered before the catch-all so they win the match. The chapters
# feed needs the ?work= query string, which a path catch-all would not expose cleanly.
@app.get("/bindery/v1/ui/api/state", dependencies=[Depends(require_token)])
async def ui_state() -> Response:
    return _json(_ui_state())


@app.get("/bindery/v1/ui/api/chapters", dependencies=[Depends(require_token)])
async def ui_chapters(work: Optional[int] = None) -> Response:
    return _json(_ui_chapters(work))


@app.get("/bindery/v1/ui/{path:path}", dependencies=[Depends(require_token)])
async def ui_get(path: str) -> Response:
    route = path.strip("/")
    if route in ("", "sources"):
        return _document(ui.shell())
    if route == "app.js":
        return _static("app.js", "text/javascript; charset=utf-8")
    if route == "style.css":
        return _static("style.css", "text/css; charset=utf-8")
    raise HTTPException(status_code=404, detail="no such UI route")


@app.post("/bindery/v1/ui/{path:path}", dependencies=[Depends(require_token)])
async def ui_post(path: str, request: Request) -> Response:
    route = path.strip("/")
    payload = await _body_json(request)
    if route == "api/order":
        store.set_manual_order(int(payload["work"]), [int(i) for i in payload.get("order", [])])
        return _json({"ok": True})
    if route == "api/pin":
        number = payload.get("number")
        store.set_pin(int(payload["work"]), int(payload["chapter"]),
                      None if number is None else float(number))
        return _json({"ok": True})
    if route == "api/reset":
        store.clear_overrides(int(payload["work"]))
        return _json({"ok": True})
    raise HTTPException(status_code=404, detail="no such UI route")


def _ui_state() -> dict:
    works = [{
        "id": w.id, "title": w.title, "author": w.author,
        "source_url": w.source_url, "hedgerow_url": w.hedgerow_url,
        "chapters": len(store.list_chapters(w.id)),
    } for w in store.list_works()]
    return {"works": works, "bot": bot.status(), "bindings": store.bindings()}


def _ui_chapters(work_id: Optional[int]) -> dict:
    if work_id is None:
        return {"chapters": []}
    ordered = merge.order_chapters(store.list_chapters(work_id))
    chapters = [{
        "id": c.id,
        "title": c.title,
        "source": c.source,
        "number": merge.effective_number(c),
        "pinned": c.pinned_number,
    } for c in ordered]
    return {"work": work_id, "chapters": chapters}


async def _body_json(request: Request) -> dict:
    raw = await request.body()
    if not raw:
        return {}
    try:
        parsed = json.loads(raw.decode("utf-8"))
        return parsed if isinstance(parsed, dict) else {}
    except (UnicodeDecodeError, json.JSONDecodeError):
        raise HTTPException(status_code=400, detail="expected a JSON object body")


def _document(html: str) -> Response:
    return Response(content=html, media_type="text/html; charset=utf-8",
                    headers={"Cache-Control": "no-store"})


def _static(name: str, content_type: str) -> Response:
    path = os.path.join(STATIC_DIR, name)
    if not os.path.exists(path):
        raise HTTPException(status_code=404, detail="missing asset")
    with open(path, "r", encoding="utf-8") as handle:
        return Response(content=handle.read(), media_type=content_type,
                        headers={"Cache-Control": "no-store"})


def _json(payload: dict) -> Response:
    return JSONResponse(content=payload, headers={"Cache-Control": "no-store"})
