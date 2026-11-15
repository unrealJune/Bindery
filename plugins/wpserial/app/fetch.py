"""The one HTTP client: urllib, a cookie jar, a politeness delay, retries, and error classes.

Every request to a serial's site goes through `Fetcher.get`, so throttling, decoding, and the
mapping of HTTP failures onto protocol error codes live in exactly one place. It is
synchronous on purpose: a download runs in a worker thread (see `main.py`), and the crawl
reads top to bottom as the sequence of page loads it is.
"""

from __future__ import annotations

import codecs
import http.cookiejar
import logging
import re
import socket
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass, field
from typing import Callable, Dict, Optional

log = logging.getLogger("bindery.wpserial")

DEFAULT_USER_AGENT = (
    "Mozilla/5.0 (compatible; Bindery-WPSerial/0.1; +https://github.com/OWNER/bindery) "
    "self-hosted ebook binder"
)
TIMEOUT = 30.0
MAX_PAGE = 16 * 1024 * 1024
MAX_IMAGE = 10 * 1024 * 1024
ATTEMPTS = 3
MAX_RETRY_AFTER = 60.0

_CHARSET = re.compile(rb"""<meta[^>]+charset=["']?([A-Za-z0-9_.:-]+)""", re.I)


class FetchError(Exception):
    """A failure fetching a page, already classified into a protocol error code."""

    def __init__(self, code: str, message: str, retryable: Optional[bool] = None, status: int = 0):
        super().__init__(message)
        self.code = code
        self.message = message
        self.status = status
        self.retryable = code in ("network", "rate_limited", "internal") if retryable is None else retryable


class Cancelled(Exception):
    """The job was cancelled; unwind without producing a result."""


@dataclass
class Response:
    url: str
    status: int
    body: bytes
    headers: Dict[str, str] = field(default_factory=dict)

    @property
    def content_type(self) -> str:
        return (self.headers.get("content-type") or "").split(";")[0].strip().lower()

    @property
    def text(self) -> str:
        return decode(self.body, self.headers.get("content-type", ""))


def _cp1252_fallback(error: UnicodeDecodeError):
    # Pages that declare UTF-8 but carry a stray Windows-1252 smart quote are common on
    # long-running blogs (old posts pasted from Word). Read the stray byte as cp1252 rather
    # than turning it into U+FFFD.
    bad = error.object[error.start:error.start + 1]
    return bad.decode("cp1252", errors="replace"), error.start + 1


codecs.register_error("wpserial-cp1252", _cp1252_fallback)


def decode(body: bytes, content_type: str = "") -> str:
    charset = None
    match = re.search(r"charset=([A-Za-z0-9_.:-]+)", content_type or "", re.I)
    if match:
        charset = match.group(1)
    else:
        sniffed = _CHARSET.search(body[:4096])
        if sniffed:
            charset = sniffed.group(1).decode("ascii", "ignore")
    charset = (charset or "utf-8").strip().lower()
    try:
        codecs.lookup(charset)
    except LookupError:
        charset = "utf-8"
    if charset in ("utf-8", "utf8"):
        return body.decode("utf-8", errors="wpserial-cp1252")
    return body.decode(charset, errors="replace")


def parse_passwords(raw: str) -> Dict[str, str]:
    """`host=password; host2=password2`, or a bare password that applies to every site.

    Returned keyed by host without `www.`, with `*` for the catch-all.
    """
    result: Dict[str, str] = {}
    for part in re.split(r"[;\n]", raw or ""):
        part = part.strip()
        if not part:
            continue
        host, sep, password = part.partition("=")
        if sep and re.fullmatch(r"[A-Za-z0-9.-]+\.[A-Za-z]{2,}", host.strip()):
            result[bare_host(host.strip())] = password.strip()
        else:
            result["*"] = part
    return result


def split(url: str) -> Optional[urllib.parse.SplitResult]:
    """`urlsplit` that answers None instead of raising. Page markup and pasted URLs both
    contain things like `http://[broken` and `:99999`, and one bad href must never abort a
    crawl or turn a probe into a 500."""
    try:
        parts = urllib.parse.urlsplit((url or "").strip())
        parts.port  # noqa: B018 - raises ValueError for an out-of-range or non-numeric port
        return parts
    except (ValueError, TypeError, AttributeError):
        return None


def to_uri(url: str) -> str:
    """Percent-encode an IRI. WordPress slugs carry non-ASCII (Worm has a `½` in one), and
    authors paste URLs straight from the address bar; `http.client` accepts only ASCII."""
    parts = split(url)
    if parts is None:
        return url
    try:
        host = (parts.hostname or "").encode("idna").decode("ascii")
    except UnicodeError:
        host = parts.hostname or ""
    netloc = host + (f":{parts.port}" if parts.port else "")
    safe = "/%:@!$&'()*+,;=~-._"
    return urllib.parse.urlunsplit((
        parts.scheme, netloc,
        urllib.parse.quote(parts.path, safe=safe),
        urllib.parse.quote(parts.query, safe=safe + "?"),
        "",
    ))


def bare_host(host_or_url: str) -> str:
    if "//" in (host_or_url or ""):
        parts = split(host_or_url)
        host = parts.hostname if parts is not None else ""
    else:
        host = host_or_url
    host = (host or "").lower().rstrip(".")
    return host[4:] if host.startswith("www.") else host


class Fetcher:
    """One visitor: a cookie jar, a user agent, and a minimum gap between requests."""

    def __init__(self, *, user_agent: str = "", delay: float = 0.75,
                 passwords: Optional[Dict[str, str]] = None,
                 cancelled: Optional[Callable[[], bool]] = None,
                 sleep: Callable[[float], None] = time.sleep):
        self.user_agent = (user_agent or "").strip() or DEFAULT_USER_AGENT
        self.delay = max(0.0, delay)
        self.passwords = passwords or {}
        self.jar = http.cookiejar.CookieJar()
        self._opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.jar))
        self._cancelled = cancelled or (lambda: False)
        self._sleep = sleep
        self._last = 0.0
        self._lock = threading.Lock()
        self.requests = 0

    # -- public

    def get(self, url: str, *, accept: str = "text/html,application/xhtml+xml;q=0.9,*/*;q=0.5",
            max_bytes: int = MAX_PAGE) -> Response:
        return self._request("GET", url, None, accept, max_bytes)

    def password_for(self, url: str) -> Optional[str]:
        host = bare_host(url)
        return self.passwords.get(host) or self.passwords.get("*")

    def unlock(self, action_url: str, page_url: str) -> bool:
        """Submit WordPress's post-password form. The cookie it sets unlocks every post
        sharing that password, so this runs at most once per password per job."""
        password = self.password_for(page_url)
        if not password:
            return False
        form = urllib.parse.urlencode({"post_password": password, "Submit": "Enter"}).encode()
        try:
            self._request("POST", action_url, form, "text/html", MAX_PAGE, referer=page_url)
        except FetchError as exc:
            log.info("post-password submission failed: %s", exc.code)
            return False
        return any(cookie.name.startswith("wp-postpass_") for cookie in self.jar)

    # -- internals

    def _request(self, method: str, url: str, data: Optional[bytes], accept: str, max_bytes: int,
                 referer: Optional[str] = None) -> Response:
        parts = split(url)
        if parts is None or parts.scheme not in ("http", "https") or not parts.hostname:
            raise FetchError("unsupported_url", f"not an http(s) URL: {url[:200]}", retryable=False)
        last_error: Optional[FetchError] = None
        for attempt in range(ATTEMPTS):
            if self._cancelled():
                raise Cancelled()
            self._throttle()
            try:
                return self._once(method, url, data, accept, max_bytes, referer)
            except FetchError as exc:
                last_error = exc
                if not exc.retryable or attempt == ATTEMPTS - 1:
                    raise
                wait = getattr(exc, "retry_after", None) or (2.0 * (2 ** attempt))
                log.info("retrying %s in %.0fs after %s", _short(url), wait, exc.code)
                self._wait(min(wait, MAX_RETRY_AFTER))
        raise last_error or FetchError("internal", "request failed")

    def _once(self, method, url, data, accept, max_bytes, referer) -> Response:
        headers = {
            "User-Agent": self.user_agent,
            "Accept": accept,
            "Accept-Language": "en;q=0.9, *;q=0.5",
        }
        if referer:
            headers["Referer"] = to_uri(referer)
        if data is not None:
            headers["Content-Type"] = "application/x-www-form-urlencoded"
        request = urllib.request.Request(to_uri(url), data=data, headers=headers, method=method)
        self.requests += 1
        try:
            with self._opener.open(request, timeout=TIMEOUT) as resp:
                body = resp.read(max_bytes + 1)
                if len(body) > max_bytes:
                    raise FetchError("parse", f"response from {_short(url)} exceeds {max_bytes} bytes",
                                     retryable=False)
                return Response(url=resp.geturl(), status=resp.status, body=body,
                                headers={k.lower(): v for k, v in resp.headers.items()})
        except urllib.error.HTTPError as exc:
            raise _classify_http(exc, url) from None
        except (urllib.error.URLError, socket.timeout, TimeoutError, ConnectionError) as exc:
            reason = getattr(exc, "reason", exc)
            raise FetchError("network", f"could not reach {_short(url)}: {reason}") from None

    def _throttle(self) -> None:
        with self._lock:
            gap = self._last + self.delay - time.monotonic()
            if gap > 0:
                self._wait(gap)
            self._last = time.monotonic()

    def _wait(self, seconds: float) -> None:
        # Sleep in slices so a cancelled job stops promptly even mid-backoff.
        deadline = time.monotonic() + seconds
        while True:
            if self._cancelled():
                raise Cancelled()
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                return
            self._sleep(min(remaining, 0.5))


def _classify_http(exc: urllib.error.HTTPError, url: str) -> FetchError:
    status = exc.code
    headers = {k.lower(): v for k, v in (exc.headers or {}).items()}
    try:
        snippet = exc.read(4096).decode("utf-8", "replace")
    except Exception:  # the body is advisory only
        snippet = ""
    if status in (404, 410):
        return FetchError("not_found", f"{_short(url)} does not exist ({status})", status=status)
    if _is_challenge(status, headers, snippet):
        return FetchError(
            "network",
            f"{_short(url)} answered with a bot challenge (HTTP {status}); this site cannot be "
            "fetched without a browser",
            retryable=False, status=status)
    if status == 429 or status == 503:
        error = FetchError("rate_limited", f"{_short(url)} is throttling requests ({status})", status=status)
        retry_after = headers.get("retry-after", "")
        if retry_after.isdigit():
            error.retry_after = float(retry_after)  # type: ignore[attr-defined]
        return error
    if status in (401, 403):
        return FetchError("auth_required", f"{_short(url)} refused access ({status})", status=status)
    if status >= 500:
        return FetchError("network", f"{_short(url)} failed upstream ({status})", status=status)
    return FetchError("internal", f"{_short(url)} answered HTTP {status}", status=status)


def _is_challenge(status: int, headers: Dict[str, str], body: str) -> bool:
    if status not in (403, 429, 503):
        return False
    if headers.get("cf-mitigated") == "challenge":
        return True
    server = headers.get("server", "").lower()
    return "cloudflare" in server and ("Just a moment" in body or "challenge-platform" in body)


def _short(url: str) -> str:
    return url if len(url) <= 120 else url[:117] + "..."
