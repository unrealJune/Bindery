"""Test harness for the Bindery plugin conformance suite.

Standard library only, on purpose: a plugin author should be able to run this with
nothing but a Python interpreter, against a container they just built, without Bindery
and without a virtualenv.
"""

from __future__ import annotations

import json
import time
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass, field
from typing import Any, Callable, Iterator, Optional

USER_AGENT = "Bindery-Conformance/1.0"


class Fail(Exception):
    """A conformance requirement was violated."""


class Skip(Exception):
    """The check does not apply to this plugin."""


# ---------------------------------------------------------------- HTTP


@dataclass
class Response:
    status: int
    headers: dict
    body: bytes

    @property
    def text(self) -> str:
        return self.body.decode("utf-8", errors="replace")

    def json(self) -> Any:
        try:
            return json.loads(self.body.decode("utf-8"))
        except Exception as exc:
            raise Fail(f"response body is not valid JSON: {exc}; got {self.body[:200]!r}")

    def header(self, name: str) -> str:
        return self.headers.get(name.lower(), "")

    @property
    def content_type(self) -> str:
        return self.header("content-type").split(";")[0].strip().lower()


class Client:
    """Minimal HTTP client that never raises on status codes."""

    def __init__(self, base_url: str, token: Optional[str] = None, timeout: float = 30.0):
        self.base_url = base_url.rstrip("/")
        self.token = token
        self.timeout = timeout

    def _build(
        self,
        method: str,
        path: str,
        *,
        body: Optional[bytes] = None,
        json_body: Any = None,
        headers: Optional[dict] = None,
        auth: bool = True,
    ) -> urllib.request.Request:
        url = self.base_url + path if path.startswith("/") else f"{self.base_url}/{path}"
        hdrs = {"User-Agent": USER_AGENT, "Accept": "*/*"}
        if json_body is not None:
            body = json.dumps(json_body).encode("utf-8")
            hdrs["Content-Type"] = "application/json"
        if auth and self.token:
            hdrs["Authorization"] = f"Bearer {self.token}"
        if headers:
            hdrs.update(headers)
        return urllib.request.Request(url, data=body, headers=hdrs, method=method.upper())

    def request(
        self,
        method: str,
        path: str,
        *,
        body: Optional[bytes] = None,
        json_body: Any = None,
        headers: Optional[dict] = None,
        auth: bool = True,
        timeout: Optional[float] = None,
        max_bytes: int = 8 * 1024 * 1024,
    ) -> Response:
        req = self._build(method, path, body=body, json_body=json_body, headers=headers, auth=auth)
        try:
            with urllib.request.urlopen(req, timeout=timeout or self.timeout) as resp:
                return Response(resp.status, _lower(resp.headers), resp.read(max_bytes))
        except urllib.error.HTTPError as err:  # a response, not an error, for our purposes
            return Response(err.code, _lower(err.headers), err.read(max_bytes))
        except urllib.error.URLError as err:
            raise Fail(f"{method} {path}: connection failed: {err.reason}")
        except TimeoutError:
            raise Fail(f"{method} {path}: timed out after {timeout or self.timeout}s")

    def stream(
        self,
        method: str,
        path: str,
        *,
        json_body: Any = None,
        headers: Optional[dict] = None,
        timeout: Optional[float] = None,
    ):
        """Open a streaming response. Returns (status, headers, file-like)."""
        req = self._build(method, path, json_body=json_body, headers=headers)
        try:
            resp = urllib.request.urlopen(req, timeout=timeout or self.timeout)
            return resp.status, _lower(resp.headers), resp
        except urllib.error.HTTPError as err:
            return err.code, _lower(err.headers), err
        except urllib.error.URLError as err:
            raise Fail(f"{method} {path}: connection failed: {err.reason}")


def _lower(headers) -> dict:
    return {k.lower(): v for k, v in headers.items()}


def read_ndjson(fp, *, max_line: int = 64 * 1024, max_events: int = 10000) -> Iterator[dict]:
    """Yield parsed NDJSON events, enforcing the protocol's line limit."""
    count = 0
    while True:
        line = fp.readline(max_line + 2)
        if not line:
            return
        if len(line) > max_line + 1:
            raise Fail(f"NDJSON line exceeds the 64 KiB limit ({len(line)} bytes)")
        text = line.decode("utf-8", errors="replace").strip()
        if not text:
            continue
        try:
            event = json.loads(text)
        except Exception as exc:
            raise Fail(f"NDJSON line is not valid JSON: {exc}; got {text[:200]!r}")
        if not isinstance(event, dict):
            raise Fail(f"NDJSON line is not a JSON object: {text[:200]!r}")
        count += 1
        if count > max_events:
            raise Fail("plugin streamed more than 10000 events without a result")
        yield event


# ---------------------------------------------------------------- checks


@dataclass
class Ctx:
    client: Client
    args: Any
    manifest: dict = field(default_factory=dict)
    notes: list = field(default_factory=list)
    state: dict = field(default_factory=dict)

    @property
    def caps(self) -> dict:
        return self.manifest.get("capabilities") or {}

    @property
    def ui_mode(self) -> str:
        return ((self.manifest.get("ui") or {}).get("mode")) or "declarative"

    def note(self, message: str) -> None:
        self.notes.append(message)


@dataclass
class Check:
    name: str
    group: str
    fn: Callable[[Ctx], None]
    requires: Optional[Callable[[Ctx], bool]]
    description: str


REGISTRY: list = []


def check(name: str, *, group: str = "protocol", requires: Optional[Callable[[Ctx], bool]] = None):
    def deco(fn):
        REGISTRY.append(Check(name, group, fn, requires, (fn.__doc__ or "").strip().splitlines()[0] if fn.__doc__ else ""))
        return fn

    return deco


# ---------------------------------------------------------------- assertions


def expect(condition: bool, message: str) -> None:
    if not condition:
        raise Fail(message)


def expect_status(resp: Response, *allowed: int) -> None:
    if resp.status not in allowed:
        want = " or ".join(str(a) for a in allowed)
        raise Fail(f"expected HTTP {want}, got {resp.status}: {resp.text[:200]}")


def expect_type(value: Any, types, where: str) -> None:
    if not isinstance(value, types):
        names = types.__name__ if isinstance(types, type) else "/".join(t.__name__ for t in types)
        raise Fail(f"{where}: expected {names}, got {type(value).__name__}")


def expect_error_body(resp: Response, where: str) -> None:
    body = resp.json()
    expect(isinstance(body, dict) and "error" in body, f"{where}: error responses carry an 'error' object")
    err = body["error"]
    expect_type(err, dict, f"{where}: error")
    expect("code" in err and "message" in err, f"{where}: error needs 'code' and 'message'")


# ---------------------------------------------------------------- runner


PASS, FAILED, SKIPPED = "pass", "fail", "skip"

GREEN, RED, YELLOW, DIM, RESET = "\033[32m", "\033[31m", "\033[33m", "\033[2m", "\033[0m"


def run_all(ctx: Ctx, *, only: Optional[str] = None, color: bool = True) -> int:
    def paint(code: str, text: str) -> str:
        return f"{code}{text}{RESET}" if color else text

    results = []
    current_group = None
    for chk in REGISTRY:
        if only and only not in chk.name and only != chk.group:
            continue
        if chk.group != current_group:
            current_group = chk.group
            print(f"\n{paint(DIM, current_group)}")
        started = time.time()
        try:
            if chk.requires is not None and not chk.requires(ctx):
                raise Skip("not applicable to this plugin")
            chk.fn(ctx)
            status, detail = PASS, ""
        except Skip as exc:
            status, detail = SKIPPED, str(exc)
        except Fail as exc:
            status, detail = FAILED, str(exc)
        except Exception as exc:  # a bug in a check should not look like a plugin failure
            status, detail = FAILED, f"harness error: {type(exc).__name__}: {exc}"
        elapsed = (time.time() - started) * 1000
        results.append((chk, status, detail))
        mark = {PASS: paint(GREEN, "PASS"), FAILED: paint(RED, "FAIL"), SKIPPED: paint(YELLOW, "SKIP")}[status]
        line = f"  {mark}  {chk.name}"
        if elapsed > 250:
            line += paint(DIM, f"  ({elapsed:.0f}ms)")
        print(line)
        if detail and status != PASS:
            print(f"        {paint(DIM if status == SKIPPED else RED, detail)}")

    passed = sum(1 for _, s, _ in results if s == PASS)
    failed = [r for r in results if r[1] == FAILED]
    skipped = sum(1 for _, s, _ in results if s == SKIPPED)

    print()
    for note in ctx.notes:
        print(f"  {paint(DIM, 'note:')} {note}")
    if ctx.notes:
        print()

    summary = f"{passed} passed, {len(failed)} failed, {skipped} skipped"
    print(paint(GREEN if not failed else RED, summary))
    if failed:
        print()
        print(paint(RED, "Failures:"))
        for chk, _, detail in failed:
            print(f"  - {chk.name}: {detail}")
    return 1 if failed else 0
