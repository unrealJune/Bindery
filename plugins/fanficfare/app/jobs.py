"""Per-job scratch directories and the artifacts they hold.

Artifacts live on disk in the plugin's own writable directory and travel to the host over
HTTP. There is deliberately no shared volume: the host and this container agree on a
protocol, not on a filesystem.
"""

from __future__ import annotations

import asyncio
import hashlib
import logging
import os
import re
import shutil
import tempfile
import time
from dataclasses import dataclass, field
from typing import Dict, Optional

log = logging.getLogger("bindery.fanficfare")

# The protocol requires at least 15 minutes of retention after the result event. Holding
# twice that costs a little disk and removes a whole class of host-side race.
RETENTION_SECONDS = 30 * 60
REAP_INTERVAL_SECONDS = 60

SAFE_ID = re.compile(r"^[A-Za-z0-9._-]{1,64}$")


@dataclass
class Artifact:
    id: str
    filename: str
    format: str
    content_type: str
    kind: str
    primary: bool
    path: str
    size: int
    sha256: str

    def describe(self) -> dict:
        return {
            "id": self.id,
            "filename": self.filename,
            "format": self.format,
            "contentType": self.content_type,
            "kind": self.kind,
            "primary": self.primary,
            "bytes": self.size,
            "sha256": self.sha256,
        }


@dataclass
class Job:
    id: str
    workdir: str
    created: float = field(default_factory=time.time)
    artifacts: Dict[str, Artifact] = field(default_factory=dict)
    cancelled: bool = False


class JobStore:
    def __init__(self, root: Optional[str] = None):
        self.root = root or os.environ.get("BINDERY_PLUGIN_WORKDIR") or tempfile.mkdtemp(prefix="bindery-fff-")
        os.makedirs(self.root, exist_ok=True)
        self._jobs: Dict[str, Job] = {}
        self._lock = asyncio.Lock()

    # -- lifecycle

    async def open(self, job_id: str) -> Job:
        if not _safe_job_id(job_id):
            raise ValueError(f"unusable job id: {job_id!r}")
        async with self._lock:
            existing = self._jobs.get(job_id)
            if existing is not None:
                return existing
            workdir = os.path.join(self.root, "jobs", job_id)
            os.makedirs(os.path.join(workdir, "out"), exist_ok=True)
            job = Job(id=job_id, workdir=workdir)
            self._jobs[job_id] = job
            return job

    async def get(self, job_id: str) -> Optional[Job]:
        async with self._lock:
            return self._jobs.get(job_id)

    async def artifact(self, job_id: str, artifact_id: str) -> Optional[Artifact]:
        if not _safe_job_id(job_id) or not SAFE_ID.match(artifact_id or ""):
            return None
        job = await self.get(job_id)
        if job is None:
            return None
        return job.artifacts.get(artifact_id)

    async def discard(self, job_id: str) -> bool:
        """Cancel and delete. Idempotent by contract."""
        if not _safe_job_id(job_id):
            return False
        async with self._lock:
            job = self._jobs.pop(job_id, None)
        if job is None:
            return False
        job.cancelled = True
        _remove(job.workdir)
        return True

    def add_artifact(self, job: Job, artifact_id: str, path: str, *, filename: str,
                     fmt: str, content_type: str, kind: str = "book", primary: bool = False) -> Artifact:
        data_size = os.path.getsize(path)
        artifact = Artifact(
            id=artifact_id,
            filename=_safe_filename(filename),
            format=fmt,
            content_type=content_type,
            kind=kind,
            primary=primary,
            path=path,
            size=data_size,
            sha256=_sha256(path),
        )
        job.artifacts[artifact_id] = artifact
        return artifact

    # -- housekeeping

    async def reap(self) -> int:
        cutoff = time.time() - RETENTION_SECONDS
        async with self._lock:
            expired = [job for job in self._jobs.values() if job.created < cutoff]
            for job in expired:
                self._jobs.pop(job.id, None)
        for job in expired:
            log.info("reaping expired job %s", job.id)
            _remove(job.workdir)
        return len(expired)

    async def reap_forever(self) -> None:
        while True:
            try:
                await asyncio.sleep(REAP_INTERVAL_SECONDS)
                await self.reap()
            except asyncio.CancelledError:
                return
            except Exception:  # a reaper that dies leaks disk forever
                log.exception("reaper iteration failed")

    def sweep_orphans(self) -> None:
        """Delete job directories left behind by a previous process."""
        jobs_root = os.path.join(self.root, "jobs")
        if not os.path.isdir(jobs_root):
            return
        for name in os.listdir(jobs_root):
            log.info("removing orphaned job directory %s", name)
            _remove(os.path.join(jobs_root, name))


def _safe_job_id(job_id: str) -> bool:
    return bool(job_id) and SAFE_ID.match(job_id) is not None


def _safe_filename(name: str) -> str:
    cleaned = os.path.basename(name or "").strip() or "book"
    cleaned = re.sub(r'[\x00-\x1f<>:"/\\|?*]', "_", cleaned)
    return cleaned[:180]


def _sha256(path: str) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for block in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def _remove(path: str) -> None:
    shutil.rmtree(path, ignore_errors=True)
