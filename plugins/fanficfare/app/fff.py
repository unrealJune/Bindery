"""Everything that knows what FanFicFare is.

FanFicFare runs as a subprocess, never in this process. It is a large library that keeps
global state, does network I/O against sites that misbehave, and occasionally wedges. A
subprocess can be killed; an import cannot.

The one exception is URL probing, which uses the library's offline adapter lookup — no
network, no state, and far more accurate than any regex.
"""

from __future__ import annotations

import asyncio
import contextlib
import io
import logging
import os
import re
import shlex
import zipfile
from dataclasses import dataclass
from typing import AsyncIterator, Optional
from xml.etree import ElementTree

log = logging.getLogger("bindery.fanficfare")

FFF_BIN = os.environ.get("BINDERY_FFF_BIN", "fanficfare")
SUPPORTED_FORMATS = ("epub", "html", "txt")

# Ordered most-specific first: the first pattern that matches wins.
ERROR_PATTERNS = [
    (re.compile(r"story does not exist", re.I), "not_found", False),
    (re.compile(r"bad story url", re.I), "unsupported_url", False),
    (re.compile(r"unknown site", re.I), "unsupported_url", False),
    (re.compile(r"timed one time password|totp", re.I), "auth_required", False),
    (re.compile(r"failed to login|login failed|password required|need username", re.I), "auth_required", False),
    (re.compile(r"adult status|adult check", re.I), "auth_required", False),
    (re.compile(r"access denied|not authorized|403", re.I), "auth_required", False),
    (re.compile(r"429|too many requests|rate.?limit", re.I), "rate_limited", True),
    (re.compile(r"timed out|timeout|connection (reset|refused|aborted)|temporary failure|dns", re.I), "network", True),
    (re.compile(r"http error 5\d\d|bad gateway|service unavailable", re.I), "network", True),
    (re.compile(r"failed to download|failed to write output", re.I), "parse", False),
    (re.compile(r"personal\.ini|regular expression failed", re.I), "internal", False),
]

RETRYABLE_BY_CODE = {
    "unsupported_url": False,
    "not_found": False,
    "auth_required": False,
    "rate_limited": True,
    "network": True,
    "parse": False,
    "cancelled": False,
    "internal": True,
}


class FffError(Exception):
    def __init__(self, code: str, message: str, retryable: Optional[bool] = None):
        super().__init__(message)
        self.code = code
        self.message = message
        self.retryable = RETRYABLE_BY_CODE.get(code, True) if retryable is None else retryable


def classify(output: str) -> FffError:
    """Turn FanFicFare's human-readable complaint into a protocol error code.

    FanFicFare's CLI prints failures with `print` and does not use its exit code to say
    what went wrong, so this reads the text. Anything unrecognized is `internal` and
    retryable, which is the safe default: a transient failure retried is cheap, a
    permanent one retried forever is not, and unknown text is more often transient.
    """
    haystack = output[-8000:]
    for pattern, code, retryable in ERROR_PATTERNS:
        if pattern.search(haystack):
            line = _best_line(haystack, pattern)
            return FffError(code, line, retryable)
    return FffError("internal", _last_meaningful_line(haystack) or "FanFicFare failed without explanation")


def _best_line(text: str, pattern) -> str:
    for line in text.splitlines():
        if pattern.search(line):
            return line.strip()[:400]
    return text.strip()[:400]


def _last_meaningful_line(text: str) -> str:
    for line in reversed(text.splitlines()):
        stripped = line.strip().strip(".")
        if stripped and not set(stripped) <= {".", " "}:
            return stripped[:400]
    return ""


# ---------------------------------------------------------------- probing


def normalize(url: str):
    """(normalized_url, domain) if FanFicFare has an adapter, else None. Offline."""
    try:
        from fanficfare import adapters  # type: ignore

        return adapters.getNormalStoryURLSite(url)
    except Exception as exc:  # a malformed URL is not an error, just unsupported
        log.debug("normalize(%s) failed: %s", url, exc)
        return None


# ---------------------------------------------------------------- config


def build_ini(config: dict) -> str:
    """Render the declarative config fields into a FanFicFare ini.

    The user's own personal.ini is written to a separate file and passed after this one,
    so it always has the last word.
    """
    defaults = []
    if _truthy(config.get("is_adult")):
        defaults.append("is_adult:true")
    if _truthy(config.get("include_images")):
        defaults.append("include_images:true")
    user_agent = (config.get("user_agent") or "").strip()
    if user_agent:
        defaults.append(f"user_agent:{user_agent}")

    sections = []
    if defaults:
        sections.append("[defaults]\n" + "\n".join(defaults))

    ao3_user = (config.get("ao3_username") or "").strip()
    ao3_pass = config.get("ao3_password") or ""
    if ao3_user:
        credentials = [f"username:{ao3_user}"]
        if ao3_pass:
            credentials.append(f"password:{ao3_pass}")
        sections.append("[archiveofourown.org]\n" + "\n".join(credentials))

    return "\n\n".join(sections) + ("\n" if sections else "")


def _truthy(value) -> bool:
    return str(value).strip().lower() in ("1", "true", "yes", "on")


def validate_ini(text: str):
    """Parse an ini the way FanFicFare will. Returns a list of human-readable problems."""
    import configparser

    problems = []
    parser = configparser.ConfigParser(interpolation=None, strict=True)
    try:
        parser.read_string(text)
    except configparser.Error as exc:
        return [str(exc).replace("\n", " ")]

    known_sections = set()
    with contextlib.suppress(Exception):
        from fanficfare import adapters  # type: ignore

        known_sections = set(adapters.getConfigSections())
    known_sections |= {"defaults", "overrides", "epub", "html", "txt", "mobi", "unknown"}

    for section in parser.sections():
        base = section.split(":")[0].strip()
        if known_sections and base not in known_sections:
            problems.append(
                f"section [{section}] does not correspond to a site FanFicFare knows; "
                "it will be ignored"
            )
    return problems


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
    """A hermetic environment: FanFicFare must not read config from anywhere but us."""
    env = dict(os.environ)
    env["HOME"] = workdir
    env["XDG_CONFIG_HOME"] = os.path.join(workdir, "xdg")
    env["PYTHONUNBUFFERED"] = "1"
    env["PYTHONIOENCODING"] = "utf-8"
    return env


async def run(args, workdir: str, timeout: float) -> Run:
    log.info("running: %s", " ".join(shlex.quote(a) for a in args))
    proc = await asyncio.create_subprocess_exec(
        *args,
        cwd=workdir,
        env=_env(workdir),
        stdout=asyncio.subprocess.PIPE,
        stderr=asyncio.subprocess.PIPE,
    )
    try:
        stdout, stderr = await asyncio.wait_for(proc.communicate(), timeout=timeout)
    except asyncio.TimeoutError:
        await _kill(proc)
        raise FffError("network", f"FanFicFare did not finish within {timeout:.0f}s", True)
    except asyncio.CancelledError:
        await _kill(proc)
        raise
    return Run(proc.returncode or 0, stdout.decode("utf-8", "replace"), stderr.decode("utf-8", "replace"))


async def run_streaming(args, workdir: str, timeout: float) -> AsyncIterator:
    """Yield ('out'|'err'|'done', payload) as FanFicFare works.

    FanFicFare's progress bar is a single dot written and flushed per network fetch, so
    reading stdout a chunk at a time is a real progress signal rather than a fiction.
    """
    proc = await asyncio.create_subprocess_exec(
        *args,
        cwd=workdir,
        env=_env(workdir),
        stdout=asyncio.subprocess.PIPE,
        stderr=asyncio.subprocess.PIPE,
    )
    stderr_chunks = []

    async def drain_stderr():
        assert proc.stderr is not None
        while True:
            line = await proc.stderr.readline()
            if not line:
                return
            stderr_chunks.append(line.decode("utf-8", "replace"))

    stderr_task = asyncio.create_task(drain_stderr())
    stdout_chunks = []
    try:
        assert proc.stdout is not None
        while True:
            try:
                chunk = await asyncio.wait_for(proc.stdout.read(64), timeout=timeout)
            except asyncio.TimeoutError:
                await _kill(proc)
                raise FffError("network", f"FanFicFare produced nothing for {timeout:.0f}s", True)
            if not chunk:
                break
            text = chunk.decode("utf-8", "replace")
            stdout_chunks.append(text)
            yield ("out", text)
        await proc.wait()
    except asyncio.CancelledError:
        await _kill(proc)
        raise
    finally:
        stderr_task.cancel()
        with contextlib.suppress(asyncio.CancelledError):
            await stderr_task
    yield ("done", Run(proc.returncode or 0, "".join(stdout_chunks), "".join(stderr_chunks)))


async def _kill(proc) -> None:
    with contextlib.suppress(ProcessLookupError):
        proc.kill()
    with contextlib.suppress(Exception):
        await proc.wait()


# ---------------------------------------------------------------- commands


def meta_args(url: str, ini_files) -> list:
    args = [FFF_BIN, "--non-interactive", "--meta-only", "--json-meta", "--no-meta-chapters", "--no-output"]
    for path in ini_files:
        args += ["-c", path]
    return args + [url]


def download_args(url: str, ini_files, out_path: str, fmt: str) -> list:
    args = [
        FFF_BIN,
        "--non-interactive",
        "--format", fmt,
        "--progressbar",
        "--json-meta-file",
        "--option", f"output_filename={out_path}",
    ]
    for path in ini_files:
        args += ["-c", path]
    return args + [url]


def list_args(url: str, ini_files) -> list:
    args = [FFF_BIN, "--non-interactive", "--normalize-list", url]
    for path in ini_files:
        args += ["-c", path]
    return args


def pick_format(preferred) -> str:
    for candidate in preferred or []:
        if candidate in SUPPORTED_FORMATS:
            return candidate
    return "epub"


# ---------------------------------------------------------------- metadata


def map_metadata(raw: dict, url: str) -> dict:
    """FanFicFare's metadata dictionary, mapped onto the protocol's."""
    series, series_index = _split_series(raw.get("series") or "")
    metadata = {
        "title": (raw.get("title") or "").strip() or "Untitled",
        "authors": _split_list(raw.get("author")),
        "summary": _strip_html(raw.get("description") or ""),
        "language": (raw.get("langcode") or "").strip() or None,
        "tags": _tags(raw),
        "sourceUrl": (raw.get("storyUrl") or url),
        "publisher": (raw.get("publisher") or raw.get("site") or "").strip() or None,
        "published": _timestamp(raw.get("datePublished")),
        "updated": _timestamp(raw.get("dateUpdated")),
    }
    if series:
        metadata["series"] = series
        if series_index is not None:
            metadata["seriesIndex"] = series_index

    chapters = _int(raw.get("numChapters"))
    if chapters is not None:
        metadata["chapters"] = chapters

    story_id = (raw.get("storyId") or "").strip()
    abbrev = (raw.get("siteabbrev") or raw.get("site") or "").strip()
    if story_id and abbrev:
        metadata["sourceId"] = f"{abbrev}:{story_id}"

    return {key: value for key, value in metadata.items() if value not in (None, "", [])}


def _split_series(value: str):
    match = re.match(r"^(.*?)\s*\[(\d+(?:\.\d+)?)\]\s*$", value.strip())
    if match:
        index = float(match.group(2))
        return match.group(1).strip(), int(index) if index.is_integer() else index
    return value.strip(), None


def _split_list(value) -> list:
    if isinstance(value, list):
        return [str(item).strip() for item in value if str(item).strip()]
    return [part.strip() for part in str(value or "").split(",") if part.strip()]


def _tags(raw: dict) -> list:
    tags = []
    for key in ("category", "genre", "characters", "ships", "warnings", "extratags"):
        tags.extend(_split_list(raw.get(key)))
    status = (raw.get("status") or "").strip()
    if status:
        tags.append(status)
    rating = (raw.get("rating") or "").strip()
    if rating:
        tags.append(rating)
    seen, unique = set(), []
    for tag in tags:
        lowered = tag.lower()
        if lowered not in seen:
            seen.add(lowered)
            unique.append(tag)
    return unique


def _int(value):
    try:
        return int(str(value).replace(",", "").strip())
    except (TypeError, ValueError):
        return None


def _timestamp(value):
    """FanFicFare dates are site-dependent. Normalize what we recognize, drop what we don't."""
    text = str(value or "").strip()
    if not text:
        return None
    if re.fullmatch(r"\d{4}-\d{2}-\d{2}", text):
        return f"{text}T00:00:00Z"
    match = re.fullmatch(r"(\d{4}-\d{2}-\d{2})[ T](\d{2}:\d{2}(?::\d{2})?)(?:\.\d+)?Z?", text)
    if match:
        time_part = match.group(2)
        if len(time_part) == 5:
            time_part += ":00"
        return f"{match.group(1)}T{time_part}Z"
    return None


TAG_RE = re.compile(r"<[^>]+>")


def _strip_html(value: str) -> str:
    import html as html_module

    text = TAG_RE.sub(" ", value)
    text = html_module.unescape(text)
    return re.sub(r"\s+", " ", text).strip()


def is_newer(remote: Optional[str], known: Optional[str]) -> bool:
    """Whether the site's copy is newer than what the host already holds.

    Unknown on either side means "assume newer" — a needless download beats silently
    never updating.
    """
    if not remote or not known:
        return True
    return remote[:19] > known[:19]


# ---------------------------------------------------------------- cover


COVER_NAME_RE = re.compile(r"(^|/)cover[^/]*\.(jpe?g|png|gif|webp)$", re.I)
IMAGE_TYPES = {
    ".jpg": "image/jpeg",
    ".jpeg": "image/jpeg",
    ".png": "image/png",
    ".gif": "image/gif",
    ".webp": "image/webp",
}


def extract_cover(epub_path: str):
    """(filename, content_type, bytes) for the EPUB's cover, or None.

    Reads the OPF properly first and only then falls back to guessing by filename.
    """
    try:
        with zipfile.ZipFile(epub_path) as book:
            names = book.namelist()
            opf_path = _opf_path(book, names)
            if opf_path:
                href = _cover_href(book, opf_path)
                if href:
                    full = _resolve(opf_path, href)
                    if full in names:
                        return _as_cover(full, book.read(full))
            for name in names:
                if COVER_NAME_RE.search(name):
                    return _as_cover(name, book.read(name))
    except (zipfile.BadZipFile, KeyError, ElementTree.ParseError, OSError) as exc:
        log.warning("cover extraction failed for %s: %s", epub_path, exc)
    return None


def _as_cover(name: str, data: bytes):
    ext = os.path.splitext(name)[1].lower()
    return f"cover{ext or '.jpg'}", IMAGE_TYPES.get(ext, "application/octet-stream"), data


def _opf_path(book: zipfile.ZipFile, names) -> Optional[str]:
    if "META-INF/container.xml" not in names:
        return next((n for n in names if n.lower().endswith(".opf")), None)
    root = ElementTree.parse(io.BytesIO(book.read("META-INF/container.xml"))).getroot()
    for element in root.iter():
        if element.tag.endswith("rootfile"):
            return element.get("full-path")
    return None


def _cover_href(book: zipfile.ZipFile, opf_path: str) -> Optional[str]:
    root = ElementTree.parse(io.BytesIO(book.read(opf_path))).getroot()
    items, cover_id = {}, None
    for element in root.iter():
        tag = element.tag.rsplit("}", 1)[-1]
        if tag == "meta" and (element.get("name") or "").lower() == "cover":
            cover_id = element.get("content")
        elif tag == "item":
            items[element.get("id")] = element
    if cover_id and cover_id in items:
        return items[cover_id].get("href")
    for element in items.values():
        if "cover-image" in (element.get("properties") or ""):
            return element.get("href")
    return None


def _resolve(opf_path: str, href: str) -> str:
    base = os.path.dirname(opf_path)
    joined = os.path.normpath(os.path.join(base, href)) if base else os.path.normpath(href)
    return joined.replace(os.sep, "/")
