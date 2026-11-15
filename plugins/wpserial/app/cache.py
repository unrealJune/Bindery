"""The plugin's own durable state, under `<workdir>/data`.

None of it is precious — losing it costs one slow re-download, never correctness — so it is
plain JSON files rather than a database:

- `chapters/` — each chapter page already read, keyed by the URL it was fetched from. This
  is what makes an update of a 300-chapter serial fetch a handful of pages instead of 300.
  Locked (password or Patreon) chapters are never cached, so unlocking one later just works.
- `images/` — image bytes, keyed by URL.
- `works/` — per-serial snapshot: the chapter URLs in order and a fingerprint of the last
  book built, so an update can honestly answer `unchanged`.
- `hosts.json` — sites this plugin has successfully bound a book from, so `probe` can claim
  further URLs on them without touching the network.

It lives beside `jobs/` (see `jobs.py`), never inside it, so the job reaper cannot delete it.
"""

from __future__ import annotations

import hashlib
import json
import logging
import os
import shutil
import tempfile
import threading
from typing import Any, Dict, List, Optional, Tuple

log = logging.getLogger("bindery.wpserial")

CACHE_VERSION = 1  # bump when the stored chapter shape or the cleaner's output changes


def _digest(value: str) -> str:
    return hashlib.sha256(value.encode("utf-8")).hexdigest()


class Cache:
    def __init__(self, root: Optional[str] = None):
        base = root or os.environ.get("BINDERY_PLUGIN_WORKDIR") or "/work"
        self.root = os.path.join(base, "data")
        self._lock = threading.Lock()
        self.writable = True
        try:
            for sub in ("chapters", "images", "works"):
                os.makedirs(os.path.join(self.root, sub), exist_ok=True)
        except OSError as exc:
            # A read-only root filesystem with no volume: run uncached rather than not at all.
            log.warning("cache disabled, %s is not writable: %s", self.root, exc)
            self.writable = False

    # -- chapters

    def get_chapters(self, url: str) -> Optional[List[Dict[str, Any]]]:
        data = self._read(os.path.join("chapters", _digest(url) + ".json"))
        if not data or data.get("v") != CACHE_VERSION or data.get("url") != url:
            return None
        return data.get("chapters")

    def put_chapters(self, url: str, chapters: List[Dict[str, Any]]) -> None:
        self._write(os.path.join("chapters", _digest(url) + ".json"),
                    {"v": CACHE_VERSION, "url": url, "host": _host(url), "chapters": chapters})

    # -- images

    def get_image(self, url: str) -> Optional[Tuple[bytes, str]]:
        if not self.writable:
            return None
        base = os.path.join(self.root, "images", _digest(url))
        try:
            with open(base + ".type", "r", encoding="utf-8") as handle:
                media_type = handle.read().strip()
            with open(base + ".bin", "rb") as handle:
                return handle.read(), media_type
        except OSError:
            return None

    def put_image(self, url: str, data: bytes, media_type: str) -> None:
        if not self.writable:
            return
        base = os.path.join(self.root, "images", _digest(url))
        try:
            _atomic_write(base + ".bin", data)
            _atomic_write(base + ".type", media_type.encode("utf-8"))
        except OSError as exc:
            log.debug("could not cache image: %s", exc)

    # -- works

    def get_work(self, source_id: str) -> Optional[Dict[str, Any]]:
        return self._read(os.path.join("works", _digest(source_id) + ".json"))

    def put_work(self, source_id: str, snapshot: Dict[str, Any]) -> None:
        self._write(os.path.join("works", _digest(source_id) + ".json"), dict(snapshot, sourceId=source_id))

    # -- hosts

    def known_hosts(self) -> List[str]:
        data = self._read("hosts.json")
        return list(data.get("hosts", [])) if data else []

    def remember_host(self, host: str) -> None:
        with self._lock:
            hosts = set(self.known_hosts())
            if host and host not in hosts:
                hosts.add(host)
                self._write("hosts.json", {"hosts": sorted(hosts)})

    # -- maintenance

    def clear(self, host: Optional[str] = None) -> int:
        """Forget cached chapters (and work snapshots) — for one site, or everything."""
        if not self.writable:
            return 0
        removed = 0
        directory = os.path.join(self.root, "chapters")
        for name in os.listdir(directory):
            path = os.path.join(directory, name)
            if host:
                data = self._read(os.path.join("chapters", name))
                if not data or data.get("host") != host:
                    continue
            try:
                os.remove(path)
                removed += 1
            except OSError:
                pass
        if not host:
            for sub in ("works", "images"):
                shutil.rmtree(os.path.join(self.root, sub), ignore_errors=True)
                os.makedirs(os.path.join(self.root, sub), exist_ok=True)
        return removed

    # -- internals

    def _read(self, relative: str) -> Optional[Dict[str, Any]]:
        if not self.writable:
            return None
        try:
            with open(os.path.join(self.root, relative), "r", encoding="utf-8") as handle:
                data = json.load(handle)
                return data if isinstance(data, dict) else None
        except (OSError, ValueError):
            return None

    def _write(self, relative: str, payload: Dict[str, Any]) -> None:
        if not self.writable:
            return
        try:
            _atomic_write(os.path.join(self.root, relative),
                          json.dumps(payload, ensure_ascii=False).encode("utf-8"))
        except OSError as exc:
            log.debug("could not write cache entry %s: %s", relative, exc)


def _atomic_write(path: str, data: bytes) -> None:
    directory = os.path.dirname(path)
    fd, temp = tempfile.mkstemp(dir=directory, prefix=".tmp-")
    try:
        with os.fdopen(fd, "wb") as handle:
            handle.write(data)
        os.replace(temp, path)
    except BaseException:
        try:
            os.remove(temp)
        except OSError:
            pass
        raise


def _host(url: str) -> str:
    from .fetch import bare_host
    return bare_host(url)

