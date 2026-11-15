"""WP Serial's HTTP surface: the plugin protocol, nothing more.

The crawl itself is synchronous (`crawl.py` reads like the sequence of page loads it is), so a
download runs it on a worker thread and relays its events onto the NDJSON stream. A client
disconnect sets a flag the fetcher checks between — and during — requests, which is how
"disconnect means cancel" reaches a thread blocked in a politeness delay.
"""

from __future__ import annotations

import asyncio
import contextlib
import hmac
import json
import logging
import os
import threading
import time
from typing import Any, AsyncIterator, Dict, List, Optional

from fastapi import Depends, FastAPI, HTTPException, Request, Response
from fastapi.exceptions import RequestValidationError
from fastapi.responses import FileResponse, JSONResponse, StreamingResponse
from pydantic import BaseModel, Field

from . import crawl, epub, site
from .cache import Cache
from .fetch import Cancelled, Fetcher, FetchError, bare_host, parse_passwords
from .jobs import JobStore
from .manifest import VERSION, build_manifest
from .routing import probe_url

logging.basicConfig(
    level=os.environ.get("BINDERY_LOG_LEVEL", "INFO").upper(),
    format="%(asctime)s %(levelname)s %(name)s %(message)s",
)
log = logging.getLogger("bindery.wpserial")

TOKEN = os.environ.get("BINDERY_PLUGIN_TOKEN") or None
ACTION_BUDGET = float(os.environ.get("WPSERIAL_ACTION_BUDGET", "25"))

jobs = JobStore()
cache = Cache()


@contextlib.asynccontextmanager
async def lifespan(app: FastAPI):
    if not TOKEN:
        log.warning("BINDERY_PLUGIN_TOKEN is unset: accepting unauthenticated requests. "
                    "This is for local development only.")
    jobs.sweep_orphans()  # per-job scratch only; the cache under data/ is never swept
    reaper = asyncio.create_task(jobs.reap_forever())
    try:
        yield
    finally:
        reaper.cancel()
        with contextlib.suppress(asyncio.CancelledError):
            await reaper


app = FastAPI(title="Bindery plugin: WP Serial", version=VERSION, lifespan=lifespan,
              docs_url=None, redoc_url=None, openapi_url=None)


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
    return {"status": "ok", "version": VERSION}


@app.get("/bindery/v1/manifest", dependencies=[Depends(require_token)])
async def manifest() -> dict:
    return build_manifest()


@app.post("/bindery/v1/probe", dependencies=[Depends(require_token)])
async def probe(body: ProbeRequest) -> dict:
    return probe_url(body.url, cache.known_hosts())


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
    return FileResponse(found.path, media_type=found.content_type, filename=found.filename,
                        headers={"Cache-Control": "no-store"})


@app.delete("/bindery/v1/jobs/{job_id}", status_code=204, dependencies=[Depends(require_token)])
async def delete_job(job_id: str) -> Response:
    await jobs.discard(job_id)  # idempotent by contract: unknown is still 204
    return Response(status_code=204)


@app.post("/bindery/v1/actions/{action}", dependencies=[Depends(require_token)])
async def run_action(action: str, body: ActionRequest) -> dict:
    if action == "inspect":
        return await _inspect(body)
    if action == "clear_cache":
        return _clear_cache(body)
    raise HTTPException(status_code=404, detail=f"no action named {action!r}")


# ---------------------------------------------------------------- the download


def _event(payload: dict) -> bytes:
    return (json.dumps(payload, ensure_ascii=False) + "\n").encode("utf-8")


def _error(code: str, message: str, retryable: Optional[bool] = None) -> dict:
    if retryable is None:
        retryable = code in ("network", "rate_limited", "internal")
    return {"event": "result", "status": "error",
            "error": {"code": code, "message": message[:1000], "retryable": retryable}}


def _int(value: Any, default: int) -> int:
    try:
        return int(str(value).strip()) if value not in (None, "") else default
    except ValueError:
        return default


def _fetcher(config: Dict[str, Any], cancelled) -> Fetcher:
    delay = max(0, min(10000, _int(config.get("request_delay_ms"), 750))) / 1000.0
    return Fetcher(user_agent=str(config.get("user_agent") or ""), delay=delay,
                   passwords=parse_passwords(str(config.get("post_passwords") or "")),
                   cancelled=cancelled)


def _truthy(value: Any, default: bool = True) -> bool:
    if value is None or value == "":
        return default
    return str(value).strip().lower() in ("true", "1", "yes", "on")


async def run_download(body: DownloadRequest, request: Request) -> AsyncIterator[bytes]:
    job = await jobs.open(body.jobId)
    log.info("job %s: download %s", job.id, body.url)
    loop = asyncio.get_running_loop()
    queue: asyncio.Queue = asyncio.Queue()
    cancel = threading.Event()

    def emit(event: dict) -> None:
        loop.call_soon_threadsafe(queue.put_nowait, event)

    work = loop.run_in_executor(None, _download_sync, body, job, emit, cancel.is_set)
    relayed = 0
    try:
        while True:
            if work.done() and queue.empty():
                break
            try:
                event = await asyncio.wait_for(queue.get(), timeout=1.0)
            except asyncio.TimeoutError:
                if await request.is_disconnected():
                    raise asyncio.CancelledError()
                continue
            yield _event(event)
            relayed += 1
            if relayed % 25 == 0 and await request.is_disconnected():
                raise asyncio.CancelledError()
        yield _event(await work)
    except asyncio.CancelledError:
        log.info("job %s: cancelled by disconnect", job.id)
        cancel.set()
        await jobs.discard(job.id)
        raise
    except Exception as exc:  # never end the stream without a result
        log.exception("job %s: unhandled failure", job.id)
        cancel.set()
        yield _event(_error("internal", f"{type(exc).__name__}: {exc}"))


def _download_sync(body: DownloadRequest, job, emit, cancelled) -> dict:
    """Runs on a worker thread. Returns the terminal `result` event."""
    config = body.config or {}
    embed = _truthy(config.get("embed_images"), True)
    fetcher = _fetcher(config, cancelled)
    crawler = crawl.Crawler(fetcher, cache, emit, embed_images=embed, cancelled=cancelled,
                            max_image_bytes=_int(config.get("max_image_kb"), 0) * 1024)
    source_id = crawl.source_id(body.url)
    previous = cache.get_work(source_id) or {}
    try:
        book = crawler.run(body.url, previous_urls=previous.get("urls"))
    except Cancelled:
        return _error("cancelled", "the job was cancelled", False)
    except crawl.CrawlError as exc:
        return _error(exc.code, exc.message, exc.retryable)
    except FetchError as exc:
        return _error(exc.code, exc.message, exc.retryable)

    emit({"event": "log", "level": "info",
          "message": f"read {len(book.chapters)} chapter(s) with {fetcher.requests} request(s)"
                     + (f"; skipped {book.skipped} link(s)" if book.skipped else "")})

    fingerprint = crawl.fingerprint(book)
    if (body.options.update and previous.get("fingerprint") == fingerprint
            and _not_newer(previous.get("updated"), body.options.knownUpdated)):
        cache.put_work(source_id, dict(previous, urls=[c.url for c in book.chapters]))
        return {"event": "result", "status": "unchanged"}

    emit({"event": "progress", "percent": 96, "message": "binding the EPUB"})
    out_dir = os.path.join(job.workdir, "out")
    book_path = os.path.join(out_dir, "book.epub")
    authors = [book.author] if book.author else []
    fallbacks = epub.build(
        book_path, title=book.title, authors=authors, language=book.language,
        identifier=epub.stable_uuid(book.source_id),
        chapters=[epub.Chapter(title=c.title, body=c.body) for c in book.chapters],
        images=book.images, cover=book.cover, description=book.description,
        source_url=book.source_url, publisher=book.site_name or None,
        published=book.published, modified=book.updated,
    )
    if fallbacks:
        emit({"event": "log", "level": "warn",
              "message": f"{fallbacks} chapter(s) had markup that could not be kept and were included as plain text"})

    artifacts = [jobs.add_artifact(job, "book", book_path, filename=crawl.filename_for(book.title),
                                   fmt="epub", content_type="application/epub+zip", kind="book", primary=True)]
    if book.cover:
        data, media_type = book.cover
        ext = crawl.IMAGE_TYPES.get(media_type, "jpg")
        cover_path = os.path.join(out_dir, f"cover.{ext}")
        with open(cover_path, "wb") as handle:
            handle.write(data)
        artifacts.append(jobs.add_artifact(job, "cover", cover_path, filename=f"cover.{ext}", fmt=ext,
                                           content_type=media_type, kind="cover", primary=False))

    cache.put_work(source_id, {"urls": [c.url for c in book.chapters], "fingerprint": fingerprint,
                               "updated": book.updated, "title": book.title})
    cache.remember_host(bare_host(body.url))

    metadata = {
        "title": book.title,
        "authors": authors,
        "summary": book.description,
        "language": book.language,
        "sourceId": book.source_id,
        "sourceUrl": book.source_url,
        "publisher": book.site_name,
        "chapters": len(book.chapters),
        "published": book.published,
        "updated": book.updated,
    }
    emit({"event": "progress", "percent": 100, "message": "done"})
    return {"event": "result", "status": "ok",
            "artifacts": [a.describe() for a in artifacts],
            "metadata": {k: v for k, v in metadata.items() if v not in (None, "", [])}}


def _not_newer(ours: Optional[str], known: Optional[str]) -> bool:
    """The host already holds at least what we last produced."""
    ours, known = site.rfc3339(ours), site.rfc3339(known)
    if not known:
        return False
    return ours is None or ours <= known


# ---------------------------------------------------------------- actions


async def _inspect(body: ActionRequest) -> dict:
    url = str(body.input.get("url") or "").strip()
    if not url:
        return {"status": "error", "error": {"code": "unsupported_url", "message": "a URL is required",
                                             "retryable": False}}
    deadline = time.monotonic() + ACTION_BUDGET
    fetcher = _fetcher(dict(body.config, request_delay_ms="0"), lambda: time.monotonic() > deadline)
    crawler = crawl.Crawler(fetcher, cache)
    try:
        plan = await asyncio.to_thread(crawler.discover, url)
    except Cancelled:
        return {"status": "error", "error": {"code": "network", "message": "the site took too long to answer",
                                             "retryable": True}}
    except (crawl.CrawlError, FetchError) as exc:
        return {"status": "error", "error": {"code": exc.code, "message": exc.message,
                                             "retryable": exc.retryable}}
    return {"status": "ok", "output": {"kind": "text", "text": describe_plan(plan)}}


def describe_plan(plan: crawl.Plan) -> str:
    lines = [
        f"Title:      {site.serial_title(plan.start, plan.toc)}",
        f"Site:       {plan.start.site_name}",
        f"Start page: {plan.start.url}",
        f"Plan:       {plan.describe()}",
    ]
    cover = site.cover_url(plan.start) or (site.cover_url(plan.toc) if plan.toc else None)
    if cover:
        lines.append(f"Cover:      {cover}")
    if plan.entries:
        lines.append("")
        shown = plan.entries if len(plan.entries) <= 12 else plan.entries[:6] + ["…"] + plan.entries[-5:]
        lines += [f"  {item}" for item in shown]
        lines.append("")
        lines.append("Links that turn out not to be chapters are skipped during the download, and")
        lines.append("chapters past the end of the list are found by following Next links.")
    return "\n".join(lines)


def _clear_cache(body: ActionRequest) -> dict:
    url = str(body.input.get("url") or "").strip()
    host = bare_host(url) if url else None
    removed = cache.clear(host)
    scope = host or "every site"
    return {"status": "ok", "output": {"kind": "message", "level": "info",
                                       "message": f"Cleared {removed} cached chapter page(s) for {scope}."}}
