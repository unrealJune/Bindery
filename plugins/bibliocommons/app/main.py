"""The BiblioCommons plugin's HTTP surface.

Every required protocol endpoint is served — this is a plugin like any other — but the
substance is §3.8 circulation. ``download`` always answers ``unsupported_url``: a library
loan is not a file, and DRM'd ebooks are borrowed in the lending platform's own app, never
copied into an OPDS library.

All network work is blocking urllib (see ``web.py``) and runs in worker threads, so one slow
library never stalls another account's request.
"""

from __future__ import annotations

import asyncio
import contextlib
import hmac
import json
import logging
import os
import time
from typing import Any, Dict, Optional, Tuple
from urllib.parse import urlencode

from fastapi import Depends, FastAPI, HTTPException, Request, Response
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse, StreamingResponse
from pydantic import BaseModel, Field

from . import account, catalog, libraries
from .manifest import VERSION, build_manifest
from .web import Browser, SiteError

logging.basicConfig(
    level=os.environ.get("BINDERY_LOG_LEVEL", "INFO").upper(),
    format="%(asctime)s %(levelname)s %(name)s %(message)s",
)
log = logging.getLogger("bindery.bibliocommons")

TOKEN = os.environ.get("BINDERY_PLUGIN_TOKEN") or None
# Under the host's 30 s action timeout, so a slow library becomes a clean error, not a hang.
CALL_TIMEOUT = float(os.environ.get("BIBLIOCOMMONS_CALL_TIMEOUT", "27"))
BRANCH_TTL = 12 * 60 * 60

sessions = account.Sessions()
_details: Dict[str, Tuple[float, libraries.Library, list]] = {}


@contextlib.asynccontextmanager
async def lifespan(_: FastAPI):
    if not TOKEN:
        log.warning("BINDERY_PLUGIN_TOKEN is unset: accepting unauthenticated requests. "
                    "This is for local development only.")
    yield


app = FastAPI(title="Bindery plugin: BiblioCommons", version=VERSION, lifespan=lifespan,
              docs_url=None, redoc_url=None, openapi_url=None)


def error_body(code: str, message: str, retryable: bool = False) -> dict:
    return {"error": {"code": code, "message": message, "retryable": retryable}}


@app.exception_handler(RequestValidationError)
async def on_validation_error(_: Request, exc: RequestValidationError) -> JSONResponse:
    # Never echo the submitted values: an account body carries a PIN.
    detail = "; ".join(".".join(str(p) for p in err.get("loc", [])[1:]) + f": {err.get('msg')}" for err in exc.errors())
    return JSONResponse(status_code=422, content=error_body("internal", detail or "invalid request body"))


@app.exception_handler(HTTPException)
async def on_http_error(_: Request, exc: HTTPException) -> Response:
    if exc.status_code == 204:
        return Response(status_code=204)
    code = {401: "auth_required", 404: "not_found", 429: "rate_limited"}.get(exc.status_code, "internal")
    return JSONResponse(status_code=exc.status_code, content=error_body(code, str(exc.detail)))


async def require_token(request: Request) -> None:
    if not TOKEN:
        return
    header = request.headers.get("authorization", "")
    if not header.startswith("Bearer ") or not hmac.compare_digest(header[7:], TOKEN):
        raise HTTPException(status_code=401, detail="missing or invalid bearer token")


# ---------------------------------------------------------------- models


class DownloadRequest(BaseModel):
    jobId: str = Field(min_length=1)
    url: str = Field(min_length=1)
    config: Dict[str, Any] = Field(default_factory=dict)
    options: Dict[str, Any] = Field(default_factory=dict)


class Account(BaseModel):
    id: str = Field(min_length=1, max_length=200)
    fields: Dict[str, str] = Field(default_factory=dict)


class AccountRequest(BaseModel):
    account: Account


class SearchRequest(AccountRequest):
    query: str = Field(min_length=1, max_length=500)
    medium: str = Field(default="any", pattern="^(any|physical|digital)$")
    page: int = Field(default=1, ge=1, le=500)


class PlaceHoldRequest(AccountRequest):
    itemId: str = Field(min_length=1, max_length=200)
    pickupLocationId: Optional[str] = Field(default=None, max_length=50)


class HoldOperationRequest(AccountRequest):
    until: Optional[str] = None


# ---------------------------------------------------------------- required endpoints


@app.get("/healthz")
async def healthz() -> dict:
    return {"status": "ok", "version": VERSION}


@app.get("/bindery/v1/manifest", dependencies=[Depends(require_token)])
async def manifest() -> dict:
    return build_manifest()


@app.post("/bindery/v1/download", dependencies=[Depends(require_token)])
async def download(body: DownloadRequest) -> StreamingResponse:
    async def stream():
        yield json.dumps({"event": "result", "status": "error", "error": {
            "code": "unsupported_url",
            "message": "the BiblioCommons plugin manages library holds; it downloads nothing",
            "retryable": False}}) + "\n"
    return StreamingResponse(stream(), media_type="application/x-ndjson", headers={"Cache-Control": "no-store"})


@app.api_route("/bindery/v1/artifacts/{job_id}/{artifact_id}", methods=["GET", "HEAD"],
               dependencies=[Depends(require_token)])
async def artifact(job_id: str, artifact_id: str):
    raise HTTPException(status_code=404, detail="this plugin produces no artifacts")


@app.delete("/bindery/v1/jobs/{job_id}", status_code=204, dependencies=[Depends(require_token)])
async def delete_job(job_id: str) -> Response:
    return Response(status_code=204)


# ---------------------------------------------------------------- circulation (§3.8)


def ok(payload: dict) -> JSONResponse:
    return JSONResponse({"status": "ok", **payload}, headers={"Cache-Control": "no-store"})


def refused(exc: SiteError) -> JSONResponse:
    return JSONResponse({"status": "error", "error": {
        "code": exc.code, "message": exc.message, "retryable": exc.retryable}},
        headers={"Cache-Control": "no-store"})


async def run(fn, *args):
    """Blocking library I/O off the event loop, bounded by the host's timeout."""
    try:
        return await asyncio.wait_for(asyncio.to_thread(fn, *args), timeout=CALL_TIMEOUT)
    except asyncio.TimeoutError:
        raise SiteError("network", "the library took too long to answer")


def _library(fields: dict) -> libraries.Library:
    try:
        return libraries.resolve(fields)
    except libraries.UnknownLibrary as exc:
        raise SiteError("internal", str(exc), retryable=False)


def _signed_in(acct: Account, *, fresh: bool = False) -> account.Session:
    card, pin = account.credentials(acct.fields)
    return sessions.get(acct.id, _library(acct.fields), card, pin, fresh=fresh)


def _with_session(acct: Account, fn):
    """Runs fn(session), signing in again once if the library has dropped the session."""
    session = _signed_in(acct)
    try:
        return fn(session)
    except SiteError as exc:
        if exc.code != "auth_required":
            raise
        sessions.forget(acct.id)
        return fn(_signed_in(acct, fresh=True))


def _search(library: libraries.Library, query: str, medium: str, page: int) -> dict:
    params = {"query": query, "searchType": "smart", "page": page, "locale": "en-US"}
    status = catalog.status_filter(medium)
    if status:
        params["f_STATUS"] = status
    response = Browser().request("GET", f"{library.gateway}/bibs/search?{urlencode(params)}").json()
    if not isinstance(response, dict) or "catalogSearch" not in response:
        raise SiteError("parse", "the library's search response has changed shape")
    library = libraries.name_from_sites(library, (response.get("entities") or {}).get("sites"))
    pagination = response["catalogSearch"].get("pagination") or {}
    return {
        "total": pagination.get("count") if isinstance(pagination.get("count"), int) else None,
        "page": page,
        "pages": pagination.get("pages") if isinstance(pagination.get("pages"), int) else None,
        "items": catalog.items_from_search(library, response, medium),
    }


def _details_for(library: libraries.Library) -> Tuple[libraries.Library, list]:
    """The library's own name and its pickup branches, from one broad public search.

    Cached for hours: neither changes often, and verify is the only caller.
    """
    cached = _details.get(library.slug)
    if cached and time.monotonic() - cached[0] < BRANCH_TTL:
        return cached[1], cached[2]
    params = urlencode({"query": "the", "searchType": "smart", "f_STATUS": catalog.PHYSICAL_FILTER, "limit": 1})
    try:
        response = Browser().request("GET", f"{library.gateway}/bibs/search?{params}").json()
        response = response if isinstance(response, dict) else {}
        named = libraries.name_from_sites(library, (response.get("entities") or {}).get("sites"))
        locations = catalog.pickup_locations(response)
    except SiteError as exc:
        if exc.code == "not_found":
            raise SiteError("internal", f"'{library.slug}' is not a BiblioCommons library this plugin can reach",
                            retryable=False)
        log.info("could not list %s branches (%s); holds will use the default branch", library.slug, exc.code)
        named, locations = library, []
    _details[library.slug] = (time.monotonic(), named, locations)
    return named, locations


def _verify(acct: Account) -> dict:
    library, locations = _details_for(_library(acct.fields))
    sessions.forget(acct.id)  # verify always proves the credentials afresh
    _signed_in(acct, fresh=True)
    card, _ = account.credentials(acct.fields)
    return {
        "library": {"id": library.slug, "name": library.name},
        "displayName": f"card ending {card[-4:]}" if len(card) >= 4 else "library card",
        "pickupLocations": locations,
    }


@app.post("/bindery/v1/circulation/verify", dependencies=[Depends(require_token)])
async def verify(body: AccountRequest) -> JSONResponse:
    try:
        return ok(await run(_verify, body.account))
    except SiteError as exc:
        return refused(exc)


@app.post("/bindery/v1/circulation/search", dependencies=[Depends(require_token)])
async def search(body: SearchRequest) -> JSONResponse:
    try:
        # Search needs the library, never the credentials (protocol §3.8).
        return ok(await run(_search, _library(body.account.fields), body.query, body.medium, body.page))
    except SiteError as exc:
        return refused(exc)


@app.post("/bindery/v1/circulation/snapshot", dependencies=[Depends(require_token)])
async def snapshot(body: AccountRequest) -> JSONResponse:
    try:
        result = await run(_with_session, body.account, account.snapshot)
        return ok({k: v for k, v in result.items() if k != "status"})
    except SiteError as exc:
        return refused(exc)


@app.post("/bindery/v1/circulation/holds", dependencies=[Depends(require_token)])
async def place_hold(body: PlaceHoldRequest) -> JSONResponse:
    try:
        await run(_with_session, body.account,
                  lambda session: account.place_hold(session, body.itemId, body.pickupLocationId))
        return ok({})
    except SiteError as exc:
        return refused(exc)


@app.post("/bindery/v1/circulation/holds/{hold_id}/{operation}", dependencies=[Depends(require_token)])
async def hold_operation(hold_id: str, operation: str, body: HoldOperationRequest) -> JSONResponse:
    if operation not in build_manifest()["circulation"]["holdOperations"]:
        raise HTTPException(status_code=404, detail=f"hold operation {operation!r} is not supported")
    try:
        await run(_with_session, body.account, lambda session: account.cancel_hold(session, hold_id))
        return ok({})
    except SiteError as exc:
        return refused(exc)
