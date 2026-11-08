"""A deliberately small HTTP client: urllib, a cookie jar, timeouts, and error classes.

Standard library on purpose, like Hedgerow's store: every module that touches the network
goes through here, so the parsers can be tested with no dependencies at all, and the image's
whole dependency tree is FastAPI and uvicorn.
"""

from __future__ import annotations

import http.cookiejar
import json
import socket
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass, field
from typing import Any, Optional

USER_AGENT = "Mozilla/5.0 (compatible; Bindery-BiblioCommons/0.1; self-hosted library client)"
TIMEOUT = 20.0
MAX_BODY = 8 * 1024 * 1024


class SiteError(Exception):
    """A failure talking to the library, already classified into a protocol error code."""

    def __init__(self, code: str, message: str, retryable: Optional[bool] = None):
        super().__init__(message)
        self.code = code
        self.message = message
        self.retryable = code in ("network", "rate_limited", "internal") if retryable is None else retryable


@dataclass
class Response:
    status: int
    url: str
    body: bytes
    headers: dict = field(default_factory=dict)

    @property
    def text(self) -> str:
        return self.body.decode("utf-8", errors="replace")

    def json(self) -> Any:
        try:
            return json.loads(self.body.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise SiteError("parse", f"the library answered with something that is not JSON ({exc.__class__.__name__})")


class Browser:
    """One cookie jar, i.e. one signed-in (or anonymous) visitor."""

    def __init__(self) -> None:
        self.jar = http.cookiejar.CookieJar()
        self._opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.jar))

    def cookie(self, name: str) -> Optional[str]:
        for cookie in self.jar:
            if cookie.name == name and (cookie.domain or "").endswith("bibliocommons.com"):
                return cookie.value
        return None

    def request(
        self,
        method: str,
        url: str,
        *,
        form: Optional[dict] = None,
        json_body: Any = None,
        headers: Optional[dict] = None,
        ok: tuple = (200,),
    ) -> Response:
        data = None
        sent = {"User-Agent": USER_AGENT, "Accept": "application/json, text/html;q=0.9"}
        if form is not None:
            data = urllib.parse.urlencode(form).encode("utf-8")
            sent["Content-Type"] = "application/x-www-form-urlencoded"
        elif json_body is not None:
            data = json.dumps(json_body).encode("utf-8")
            sent["Content-Type"] = "application/json"
        sent.update(headers or {})

        request = urllib.request.Request(url, data=data, headers=sent, method=method)
        try:
            with self._opener.open(request, timeout=TIMEOUT) as reply:
                body = reply.read(MAX_BODY + 1)
                if len(body) > MAX_BODY:
                    raise SiteError("parse", "the library's response was implausibly large")
                response = Response(reply.status, reply.geturl(), body, dict(reply.headers))
        except urllib.error.HTTPError as exc:
            response = Response(exc.code, url, exc.read(64 * 1024) if exc.fp else b"", dict(exc.headers or {}))
        except (urllib.error.URLError, socket.timeout, TimeoutError, ConnectionError) as exc:
            reason = getattr(exc, "reason", exc)
            raise SiteError("network", f"could not reach the library ({reason})")

        if response.status in ok:
            return response
        raise classify(response)


def classify(response: Response) -> SiteError:
    error = {}
    try:
        body = json.loads(response.body.decode("utf-8")) if response.body else {}
        error = (body.get("error") if isinstance(body, dict) else None) or {}
    except (UnicodeDecodeError, json.JSONDecodeError):
        pass
    # The gateway answers 403 for a subdomain that is not (or no longer) a public BiblioCommons
    # catalog. That is a wrong library, not a wrong PIN, and must not be reported as one.
    if isinstance(error, dict) and error.get("code") == "not_available":
        return SiteError("not_found", "this library's catalog is not available through BiblioCommons", retryable=False)
    if response.status in (401, 403):
        return SiteError("auth_required", "the library refused the session")
    if response.status == 404:
        return SiteError("not_found", "the library has no such record")
    if response.status == 429:
        return SiteError("rate_limited", "the library is throttling requests")
    if response.status >= 500:
        return SiteError("network", f"the library returned {response.status}")
    detail = str(error.get("message") or "") if isinstance(error, dict) else ""
    return SiteError("internal", f"the library returned {response.status}{': ' + detail[:200] if detail else ''}",
                     retryable=False)
