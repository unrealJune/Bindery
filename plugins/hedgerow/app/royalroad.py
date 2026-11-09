"""Royal Road, fetched through FanFicFare as a subprocess.

Royal Road is already a FanFicFare-supported site, so there is no scraper here and there
must never be one. FanFicFare runs as a child process exactly as it does in the reference
plugin — it is a large, stateful, network-touching library, and a subprocess can be killed
where an import cannot. The single in-process use is the offline adapter lookup for probing,
which is pure.
"""

from __future__ import annotations

import asyncio
import contextlib
import logging
import os
import re
import shlex
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass
from typing import List, Optional, Tuple

log = logging.getLogger("bindery.hedgerow")

FFF_BIN = os.environ.get("BINDERY_FFF_BIN", "fanficfare")

# Royal Road fiction and chapter URLs, plus the author's own mirror. hungryshedgerow.net is
# not a FanFicFare site — it is matched so its URLs route here, where the work is served
# from ingested Discord copies rather than a live scrape.
ROYALROAD_RE = re.compile(r"^https?://(www\.)?royalroad\.com/fiction/\d+", re.I)
HEDGEROW_RE = re.compile(r"^https?://(www\.)?hungryshedgerow\.net(/|$)", re.I)

ERROR_PATTERNS = [
    (re.compile(r"story does not exist", re.I), "not_found", False),
    (re.compile(r"bad story url|unknown site", re.I), "unsupported_url", False),
    (re.compile(r"failed to login|login failed|password required", re.I), "auth_required", False),
    (re.compile(r"access denied|forbidden|\b403\b", re.I), "auth_required", False),
    (re.compile(r"429|too many requests|rate.?limit", re.I), "rate_limited", True),
    (re.compile(r"timed out|timeout|connection (reset|refused|aborted)|dns", re.I), "network", True),
    (re.compile(r"http error 5\d\d|bad gateway|service unavailable", re.I), "network", True),
    (re.compile(r"failed to download|failed to write output", re.I), "parse", False),
]

RETRYABLE_BY_CODE = {
    "unsupported_url": False, "not_found": False, "auth_required": False,
    "rate_limited": True, "network": True, "parse": False, "cancelled": False, "internal": True,
}


class RoyalRoadError(Exception):
    def __init__(self, code: str, message: str, retryable: Optional[bool] = None):
        super().__init__(message)
        self.code = code
        self.message = message
        self.retryable = RETRYABLE_BY_CODE.get(code, True) if retryable is None else retryable


def classify(output: str) -> RoyalRoadError:
    haystack = output[-8000:]
    for pattern, code, retryable in ERROR_PATTERNS:
        if pattern.search(haystack):
            for line in haystack.splitlines():
                if pattern.search(line):
                    return RoyalRoadError(code, line.strip()[:400], retryable)
    return RoyalRoadError("internal", "FanFicFare failed without a recognizable reason")


# ---------------------------------------------------------------- bot checks

CHALLENGE_TIMEOUT = 10.0


def is_challenge(status: int, headers) -> bool:
    """Cloudflare's "Just a moment..." interstitial, by the header it marks it with."""
    return status in (403, 503) and (headers.get("cf-mitigated") or "").lower() == "challenge"


def _probe(url: str) -> bool:
    try:
        request = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0", "Accept": "text/html"})
        with urllib.request.urlopen(request, timeout=CHALLENGE_TIMEOUT) as reply:
            return is_challenge(reply.status, reply.headers)
    except urllib.error.HTTPError as refused:
        with refused:
            return is_challenge(refused.code, refused.headers)
    except (urllib.error.URLError, OSError, ValueError) as exc:
        log.warning("could not diagnose the browser challenge (%s); keeping the original error", type(exc).__name__)
        return False


async def explain(error: RoyalRoadError, url: str) -> RoyalRoadError:
    """Tell a site's bot check apart from a refused login.

    FanFicFare reports both as a bare 403, and the challenge page never reaches its output,
    so one plain request to the same URL is how to know. A bot check is not the person's
    credentials; separate browser integrations are documented in the README. This only
    diagnoses the failure and makes no attempt to pass the check.
    """
    if error.code != "auth_required" or "403" not in error.message:
        return error
    if not await asyncio.to_thread(_probe, url):
        return error
    host = urllib.parse.urlsplit(url).hostname or "the site"
    message = f"{host} is showing automated downloads a Cloudflare bot check. This downloader was blocked, not a confirmed login failure."
    if host in ("royalroad.com", "www.royalroad.com"):
        message += " Use Calibre's FanFicFare/Camoufox integration, then upload the EPUB through Intake (see the plugin README)."
    return RoyalRoadError("network", message)


# ---------------------------------------------------------------- classification


def is_royalroad(url: str) -> bool:
    return bool(ROYALROAD_RE.match(url or ""))


def is_hedgerow(url: str) -> bool:
    return bool(HEDGEROW_RE.match(url or ""))


def claims(url: str) -> bool:
    return is_royalroad(url) or is_hedgerow(url)


def normalize(url: str) -> Optional[Tuple[str, str]]:
    """(normalized_url, domain) if FanFicFare has an adapter, else None. Offline, pure.

    Falls back to a plain pattern check when FanFicFare is not importable, so probing still
    answers for Royal Road on a box without the library installed.
    """
    try:
        from fanficfare import adapters  # type: ignore

        result = adapters.getNormalStoryURLSite(url)
        if result:
            return result
    except Exception as exc:  # a malformed URL is unsupported, not an error
        log.debug("normalize(%s) failed: %s", url, exc)
    if is_royalroad(url):
        return (url, "royalroad.com")
    return None


# ---------------------------------------------------------------- running


@dataclass
class Run:
    returncode: int
    stdout: str
    stderr: str

    @property
    def output(self) -> str:
        return self.stdout + "\n" + self.stderr


def _env(workdir: str) -> dict:
    """Hermetic: FanFicFare reads configuration from us and nowhere else."""
    env = dict(os.environ)
    env["HOME"] = workdir
    env["XDG_CONFIG_HOME"] = os.path.join(workdir, "xdg")
    env["PYTHONUNBUFFERED"] = "1"
    env["PYTHONIOENCODING"] = "utf-8"
    return env


async def run(args: List[str], workdir: str, timeout: float) -> Run:
    log.info("running: %s", " ".join(shlex.quote(a) for a in args))
    proc = await asyncio.create_subprocess_exec(
        *args, cwd=workdir, env=_env(workdir),
        stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE,
    )
    try:
        stdout, stderr = await asyncio.wait_for(proc.communicate(), timeout=timeout)
    except asyncio.TimeoutError:
        await _kill(proc)
        raise RoyalRoadError("network", f"Royal Road fetch did not finish within {timeout:.0f}s", True)
    except asyncio.CancelledError:
        await _kill(proc)
        raise
    return Run(proc.returncode or 0, stdout.decode("utf-8", "replace"), stderr.decode("utf-8", "replace"))


async def _kill(proc) -> None:
    with contextlib.suppress(ProcessLookupError):
        proc.kill()
    with contextlib.suppress(Exception):
        await proc.wait()


# ---------------------------------------------------------------- commands


def build_ini(config: dict) -> str:
    """A FanFicFare ini from the declarative config. Royal Road rarely needs auth, so this
    is mostly the adult gate and a user-agent override."""
    defaults = []
    if _truthy(config.get("is_adult")):
        defaults.append("is_adult:true")
    user_agent = (config.get("user_agent") or "").strip()
    if user_agent:
        defaults.append(f"user_agent:{user_agent}")
    return "[defaults]\n" + "\n".join(defaults) + "\n" if defaults else ""


def _truthy(value) -> bool:
    return str(value).strip().lower() in ("1", "true", "yes", "on")


def meta_args(url: str, ini_files: List[str]) -> List[str]:
    args = [FFF_BIN, "--non-interactive", "--meta-only", "--json-meta", "--no-meta-chapters", "--no-output"]
    for path in ini_files:
        args += ["-c", path]
    return args + [url]


def download_args(url: str, ini_files: List[str], out_path: str) -> List[str]:
    args = [
        FFF_BIN, "--non-interactive", "--format", "epub", "--progressbar",
        "--json-meta-file", "--option", f"output_filename={out_path}",
    ]
    for path in ini_files:
        args += ["-c", path]
    return args + [url]


# ---------------------------------------------------------------- metadata


def map_metadata(raw: dict, url: str) -> dict:
    story_id = (raw.get("storyId") or "").strip()
    abbrev = (raw.get("siteabbrev") or raw.get("site") or "").strip()
    metadata = {
        "title": (raw.get("title") or "").strip() or "Untitled",
        "author": _first_author(raw.get("author")),
        "chapters": _int(raw.get("numChapters")),
        "published": _timestamp(raw.get("datePublished")),
        "updated": _timestamp(raw.get("dateUpdated")),
        "sourceUrl": raw.get("storyUrl") or url,
        "sourceId": f"{abbrev}:{story_id}" if (story_id and abbrev) else None,
    }
    return {k: v for k, v in metadata.items() if v not in (None, "", [])}


def _first_author(value) -> str:
    if isinstance(value, list):
        return str(value[0]).strip() if value else ""
    return str(value or "").split(",")[0].strip()


def _int(value):
    try:
        return int(str(value).replace(",", "").strip())
    except (TypeError, ValueError):
        return None


def _timestamp(value):
    text = str(value or "").strip()
    if re.fullmatch(r"\d{4}-\d{2}-\d{2}", text):
        return f"{text}T00:00:00Z"
    match = re.fullmatch(r"(\d{4}-\d{2}-\d{2})[ T](\d{2}:\d{2}(?::\d{2})?)(?:\.\d+)?Z?", text)
    if match:
        time_part = match.group(2)
        if len(time_part) == 5:
            time_part += ":00"
        return f"{match.group(1)}T{time_part}Z"
    return None
