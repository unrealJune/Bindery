"""FanFicFare as a Bindery plugin.

Six protocol endpoints, a fragment UI, and one action. No Bindery code, no shared volume,
no import of anything on the host side — this container and Bindery agree on
`docs/PLUGIN-PROTOCOL.md` and nothing else.
"""

from __future__ import annotations

import asyncio
import contextlib
import hmac
import json
import logging
import os
import time
from typing import Any, Dict, List, Optional

from fastapi import Depends, FastAPI, Form, HTTPException, Query, Request, Response
from fastapi.exceptions import RequestValidationError
from fastapi.responses import FileResponse, JSONResponse, StreamingResponse
from pydantic import BaseModel, Field

from . import fff, ui
from .jobs import JobStore
from .manifest import VERSION, build_manifest

logging.basicConfig(
    level=os.environ.get("BINDERY_LOG_LEVEL", "INFO").upper(),
    format="%(asctime)s %(levelname)s %(name)s %(message)s",
)
log = logging.getLogger("bindery.fanficfare")

TOKEN = os.environ.get("BINDERY_PLUGIN_TOKEN") or None
META_TIMEOUT = float(os.environ.get("BINDERY_FFF_META_TIMEOUT", "120"))
DOWNLOAD_IDLE_TIMEOUT = float(os.environ.get("BINDERY_FFF_IDLE_TIMEOUT", "300"))
ACTION_TIMEOUT = float(os.environ.get("BINDERY_FFF_ACTION_TIMEOUT", "28"))

store = JobStore()


# ---------------------------------------------------------------- app


@contextlib.asynccontextmanager
async def lifespan(app: FastAPI):
    if not TOKEN:
        log.warning("BINDERY_PLUGIN_TOKEN is unset: accepting unauthenticated requests. "
                    "This is for local development only.")
    store.sweep_orphans()
    build_manifest()  # fail loudly at boot if FanFicFare is not importable
    reaper = asyncio.create_task(store.reap_forever())
    try:
        yield
    finally:
        reaper.cancel()
        with contextlib.suppress(asyncio.CancelledError):
            await reaper


app = FastAPI(
    title="Bindery plugin: FanFicFare",
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
    """Bearer auth on /bindery/v1 only. /healthz stays open for liveness probes."""
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


# ---------------------------------------------------------------- endpoints


@app.get("/healthz")
async def healthz() -> dict:
    return {"status": "ok", "version": VERSION, "fanficfare": _fff_version()}


def _fff_version() -> str:
    from .manifest import fanficfare_version

    return fanficfare_version()


@app.get("/bindery/v1/manifest", dependencies=[Depends(require_token)])
async def manifest() -> dict:
    return build_manifest()


@app.post("/bindery/v1/probe", dependencies=[Depends(require_token)])
async def probe(body: ProbeRequest) -> dict:
    result = fff.normalize(body.url)
    if not result:
        return {"supported": False, "confidence": 0.0, "reason": "no FanFicFare adapter claims this URL"}
    normalized, domain = result
    return {"supported": True, "confidence": 1.0, "reason": f"{domain} adapter, normalized to {normalized}"}


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
    found = await store.artifact(job_id, artifact_id)
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
    await store.discard(job_id)  # idempotent by contract: unknown is still 204
    return Response(status_code=204)


@app.post("/bindery/v1/actions/{action}", dependencies=[Depends(require_token)])
async def run_action(action: str, body: ActionRequest) -> dict:
    if action != "list_urls":
        raise HTTPException(status_code=404, detail=f"no action named {action!r}")

    url = str(body.input.get("url") or "").strip()
    if not url:
        return {"status": "error", "error": {"code": "internal", "message": "'url' is required",
                                             "retryable": False}}

    job = await store.open(f"action-{int(time.time() * 1000)}")
    try:
        ini_files = _write_ini(job.workdir, body.config)
        result = await fff.run(fff.list_args(url, ini_files), job.workdir, timeout=ACTION_TIMEOUT)
        urls = [line.strip() for line in result.stdout.splitlines()
                if line.strip().startswith(("http://", "https://"))]
        if not urls:
            error = fff.classify(result.output)
            if result.returncode == 0 and error.code == "internal":
                return {"status": "ok", "output": {"kind": "list", "items": []}}
            return {"status": "error", "error": {"code": error.code, "message": error.message,
                                                 "retryable": error.retryable}}
        return {"status": "ok", "output": {"kind": "list", "items": [_list_item(u) for u in urls]}}
    except fff.FffError as exc:
        return {"status": "error", "error": {"code": exc.code, "message": exc.message,
                                             "retryable": exc.retryable}}
    finally:
        await store.discard(job.id)


def _list_item(url: str) -> dict:
    from urllib.parse import urlparse

    parsed = urlparse(url)
    return {"title": url, "subtitle": parsed.netloc, "url": url}


# ---------------------------------------------------------------- UI


@app.get("/bindery/v1/ui/{path:path}", dependencies=[Depends(require_token)])
async def ui_get(path: str, request: Request, url: Optional[str] = Query(default=None),
                 q: Optional[str] = Query(default=None)) -> Response:
    base = _base(request)
    route = "/" + path.strip("/")
    if route == "/":
        return _html(ui.home(base))
    if route == "/check":
        return _html(ui.check(base, url))
    if route == "/sites":
        return _html(ui.sites(base, q))
    if route == "/sites/list":
        return _html(ui.site_list(q))
    if route == "/ini":
        return _html(ui.ini(base))
    return _html(ui.not_found(), status=404)


@app.post("/bindery/v1/ui/{path:path}", dependencies=[Depends(require_token)])
async def ui_post(path: str, ini: str = Form(default="")) -> Response:
    route = "/" + path.strip("/")
    if route == "/ini/validate":
        return _html(ui.ini_result(ini))
    return _html(ui.not_found(), status=404)


def _base(request: Request) -> str:
    return (request.headers.get("x-bindery-base") or "/plugins/fanficfare/ui").rstrip("/")


def _html(fragment: str, status: int = 200) -> Response:
    return Response(content=fragment, status_code=status, media_type="text/html; charset=utf-8",
                    headers={"Cache-Control": "no-store"})


# ---------------------------------------------------------------- the download itself


def _write_ini(workdir: str, config: Dict[str, Any]) -> List[str]:
    """Two files, in priority order: ours, then the user's, which therefore wins."""
    paths = []
    generated = fff.build_ini(config)
    if generated.strip():
        generated_path = os.path.join(workdir, "bindery.ini")
        with open(generated_path, "w", encoding="utf-8") as handle:
            handle.write(generated)
        paths.append(generated_path)

    personal = config.get("personal_ini") or ""
    if personal.strip():
        personal_path = os.path.join(workdir, "personal-user.ini")
        with open(personal_path, "w", encoding="utf-8") as handle:
            handle.write(personal)
        paths.append(personal_path)
    return paths


def _event(payload: dict) -> bytes:
    return (json.dumps(payload, ensure_ascii=False) + "\n").encode("utf-8")


async def run_download(body: DownloadRequest, request: Request):
    job = await store.open(body.jobId)
    log.info("job %s: download %s", job.id, body.url)
    try:
        async for chunk in _download_phases(body, job, request):
            yield chunk
    except asyncio.CancelledError:
        # The host hung up. Stop working and leave nothing behind.
        log.info("job %s: cancelled by disconnect", job.id)
        await store.discard(job.id)
        raise
    except fff.FffError as exc:
        log.warning("job %s: %s: %s", job.id, exc.code, exc.message)
        yield _event({"event": "result", "status": "error",
                      "error": {"code": exc.code, "message": exc.message, "retryable": exc.retryable}})
    except Exception as exc:  # never end the stream without a result event
        log.exception("job %s: unhandled failure", job.id)
        yield _event({"event": "result", "status": "error",
                      "error": {"code": "internal", "message": f"{type(exc).__name__}: {exc}",
                                "retryable": True}})


async def _download_phases(body: DownloadRequest, job, request: Request):
    normalized = fff.normalize(body.url)
    if not normalized:
        yield _event({"event": "result", "status": "error", "error": {
            "code": "unsupported_url",
            "message": "no FanFicFare adapter claims this URL",
            "retryable": False}})
        return

    url = normalized[0]
    ini_files = _write_ini(job.workdir, body.config)
    fmt = fff.pick_format(body.options.formats)

    # Phase 1 - metadata only. Cheap, gives the host a title immediately, and lets an
    # update finish without downloading a single chapter.
    yield _event({"event": "progress", "percent": 2, "message": "reading story metadata"})
    meta_run = await fff.run(fff.meta_args(url, ini_files), job.workdir, timeout=META_TIMEOUT)
    raw_meta = _parse_json(meta_run.stdout)
    if raw_meta is None:
        raise fff.classify(meta_run.output)

    metadata = fff.map_metadata(raw_meta, url)
    chapters = metadata.get("chapters") or 1
    yield _event({"event": "log", "level": "info",
                  "message": f"{metadata['title']} - {chapters} chapter(s)"})

    if body.options.update and not fff.is_newer(metadata.get("updated"), body.options.knownUpdated):
        yield _event({"event": "log", "level": "info",
                      "message": f"no change since {body.options.knownUpdated}"})
        yield _event({"event": "result", "status": "unchanged"})
        return

    # Phase 2 - the download. One dot per network fetch on stdout is FanFicFare's own
    # progress signal; against a known chapter count it is a usable percentage.
    yield _event({"event": "progress", "percent": 8, "message": f"fetching {chapters} chapter(s)"})
    out_relative = os.path.join("out", f"story.{fmt}")
    out_path = os.path.join(job.workdir, out_relative)
    expected_fetches = max(chapters + 2, 3)
    fetches = 0
    last_emit = 0.0
    finished = None

    async for kind, payload in fff.run_streaming(
        fff.download_args(url, ini_files, out_relative, fmt), job.workdir, timeout=DOWNLOAD_IDLE_TIMEOUT
    ):
        if kind == "done":
            finished = payload
            break
        fetches += payload.count(".")
        now = time.monotonic()
        if now - last_emit >= 0.5:
            last_emit = now
            percent = 8 + 87 * min(1.0, fetches / expected_fetches)
            yield _event({"event": "progress", "percent": round(percent, 1),
                          "message": f"fetched {min(fetches, chapters)}/{chapters}"})
            if await request.is_disconnected():
                raise asyncio.CancelledError()

    if finished is None or not os.path.exists(out_path):
        raise fff.classify(finished.output if finished else "FanFicFare produced no output file")

    yield _event({"event": "progress", "percent": 96, "message": "packaging"})

    final_meta = _parse_json_file(out_path + ".json") or raw_meta
    metadata = fff.map_metadata(final_meta, url)

    artifacts = [store.add_artifact(
        job, "book", out_path,
        filename=_filename(metadata, fmt),
        fmt=fmt,
        content_type=_content_type(fmt),
        kind="book",
        primary=True,
    )]

    cover = fff.extract_cover(out_path) if fmt == "epub" else None
    if cover:
        cover_name, cover_type, cover_bytes = cover
        cover_path = os.path.join(job.workdir, "out", cover_name)
        with open(cover_path, "wb") as handle:
            handle.write(cover_bytes)
        artifacts.append(store.add_artifact(
            job, "cover", cover_path,
            filename=cover_name,
            fmt=os.path.splitext(cover_name)[1].lstrip(".").lower() or "jpg",
            content_type=cover_type,
            kind="cover",
            primary=False,
        ))

    yield _event({"event": "result", "status": "ok",
                  "artifacts": [item.describe() for item in artifacts],
                  "metadata": metadata})


def _filename(metadata: dict, fmt: str) -> str:
    title = (metadata.get("title") or "story").strip()
    authors = metadata.get("authors") or []
    stem = f"{title} - {authors[0]}" if authors else title
    return f"{stem}.{fmt}"


def _content_type(fmt: str) -> str:
    return {
        "epub": "application/epub+zip",
        "html": "text/html",
        "txt": "text/plain; charset=utf-8",
        "mobi": "application/x-mobipocket-ebook",
    }.get(fmt, "application/octet-stream")


def _parse_json(text: str):
    """FanFicFare prints JSON to stdout; anything else there means it failed."""
    start = text.find("{")
    end = text.rfind("}")
    if start < 0 or end <= start:
        return None
    try:
        return json.loads(text[start:end + 1])
    except json.JSONDecodeError:
        return None


def _parse_json_file(path: str):
    try:
        with open(path, "r", encoding="utf-8") as handle:
            return json.load(handle)
    except (OSError, json.JSONDecodeError):
        return None
