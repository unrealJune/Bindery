"""Durable state, in SQLite, on the plugin's own private volume.

Unlike the FanFicFare plugin — whose scratch is disposable — Hedgerow has to remember
things across restarts: which channel is bound to which work, every chapter it has ingested
and *which source supplied it first*, and any manual ordering the user has imposed. That
lives here, under `data/`, next to but never mixed with the per-job scratch under `jobs/`.

This is the plugin's own volume. It is not shared with the host, so the one rule still
holds: artifacts still travel to Bindery over HTTP, and nothing here is a shared mount.
"""

from __future__ import annotations

import os
import sqlite3
import threading
import time
from dataclasses import dataclass
from typing import Iterable, Optional

WORKDIR = os.environ.get("BINDERY_PLUGIN_WORKDIR") or "/work"
DATA_DIR = os.path.join(WORKDIR, "data")
DB_PATH = os.path.join(DATA_DIR, "hedgerow.db")

# 'royalroad' or 'discord'. Stored verbatim on every chapter so first-seen precedence can
# be shown in the UI and never silently rewritten.
SOURCE_ROYALROAD = "royalroad"
SOURCE_DISCORD = "discord"

# Which parser version the stored `chapter_key` values were computed with. See
# `reindex_chapters`.
SETTING_KEY_VERSION = "chapter_key_version"

SCHEMA = """
CREATE TABLE IF NOT EXISTS settings (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS works (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    source_url    TEXT UNIQUE,          -- the Royal Road fiction URL, when known
    hedgerow_url  TEXT,                 -- the hungryshedgerow.net mirror, when known
    source_id     TEXT,                 -- stable identity, e.g. rr:12345
    title         TEXT NOT NULL DEFAULT 'Untitled',
    author        TEXT NOT NULL DEFAULT '',
    created       REAL NOT NULL
);

CREATE TABLE IF NOT EXISTS channel_bindings (
    channel_id TEXT PRIMARY KEY,
    work_id    INTEGER NOT NULL REFERENCES works(id) ON DELETE CASCADE,
    bound_at   REAL NOT NULL
);

CREATE TABLE IF NOT EXISTS chapters (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    work_id       INTEGER NOT NULL REFERENCES works(id) ON DELETE CASCADE,
    chapter_key   TEXT NOT NULL,        -- identity for first-seen dedup
    source        TEXT NOT NULL,        -- source that FIRST supplied this chapter
    title         TEXT NOT NULL,
    arc           INTEGER,              -- parsed 'arc' of arc.part, when present
    part          INTEGER,              -- parsed 'part' of arc.part, when present
    published     TEXT,                 -- RFC3339, from Royal Road, when present
    discord_ts    REAL,                 -- Discord message timestamp, when present
    pinned_number REAL,                 -- manual explicit number, overrides parsing
    manual_order  INTEGER,              -- manual drag-and-drop rank, overrides everything
    body_html     TEXT NOT NULL DEFAULT '',
    first_seen    REAL NOT NULL,
    UNIQUE(work_id, chapter_key)
);
"""


@dataclass
class Work:
    id: int
    source_url: Optional[str]
    hedgerow_url: Optional[str]
    source_id: Optional[str]
    title: str
    author: str


@dataclass
class Chapter:
    id: int
    work_id: int
    chapter_key: str
    source: str
    title: str
    arc: Optional[int]
    part: Optional[int]
    published: Optional[str]
    discord_ts: Optional[float]
    pinned_number: Optional[float]
    manual_order: Optional[int]
    first_seen: float


class Store:
    """A thin, synchronous SQLite wrapper.

    Every call is short and I/O-bound against a local file, so it runs on whatever thread
    calls it under a single lock rather than dragging in an async driver. The lock is what
    lets the Discord bot's event loop and the request handlers share one connection safely.
    """

    def __init__(self, db_path: str = DB_PATH):
        os.makedirs(os.path.dirname(db_path), exist_ok=True)
        self._lock = threading.Lock()
        self._db = sqlite3.connect(db_path, check_same_thread=False)
        self._db.row_factory = sqlite3.Row
        self._db.execute("PRAGMA journal_mode=WAL")
        self._db.execute("PRAGMA foreign_keys=ON")
        with self._lock:
            self._db.executescript(SCHEMA)
            self._db.commit()

    # ------------------------------------------------------------ settings

    def get_setting(self, key: str) -> Optional[str]:
        with self._lock:
            row = self._db.execute("SELECT value FROM settings WHERE key=?", (key,)).fetchone()
        return row["value"] if row else None

    def set_setting(self, key: str, value: str) -> None:
        with self._lock:
            self._db.execute(
                "INSERT INTO settings(key, value) VALUES(?, ?) "
                "ON CONFLICT(key) DO UPDATE SET value=excluded.value",
                (key, value),
            )
            self._db.commit()

    # ------------------------------------------------------------ works

    def get_or_create_work(
        self,
        *,
        source_url: Optional[str] = None,
        hedgerow_url: Optional[str] = None,
        source_id: Optional[str] = None,
        title: str = "Untitled",
        author: str = "",
    ) -> Work:
        with self._lock:
            row = None
            if source_url:
                row = self._db.execute(
                    "SELECT * FROM works WHERE source_url=?", (source_url,)
                ).fetchone()
            if row is None and hedgerow_url:
                row = self._db.execute(
                    "SELECT * FROM works WHERE hedgerow_url=?", (hedgerow_url,)
                ).fetchone()
            if row is None:
                cur = self._db.execute(
                    "INSERT INTO works(source_url, hedgerow_url, source_id, title, author, created) "
                    "VALUES(?, ?, ?, ?, ?, ?)",
                    (source_url, hedgerow_url, source_id, title, author, time.time()),
                )
                self._db.commit()
                row = self._db.execute("SELECT * FROM works WHERE id=?", (cur.lastrowid,)).fetchone()
            return _work(row)

    def update_work(self, work_id: int, *, title: Optional[str] = None,
                    author: Optional[str] = None, source_id: Optional[str] = None) -> None:
        sets, params = [], []
        if title:
            sets.append("title=?"); params.append(title)
        if author is not None:
            sets.append("author=?"); params.append(author)
        if source_id:
            sets.append("source_id=?"); params.append(source_id)
        if not sets:
            return
        params.append(work_id)
        with self._lock:
            self._db.execute(f"UPDATE works SET {', '.join(sets)} WHERE id=?", params)
            self._db.commit()

    def get_work(self, work_id: int) -> Optional[Work]:
        with self._lock:
            row = self._db.execute("SELECT * FROM works WHERE id=?", (work_id,)).fetchone()
        return _work(row) if row else None

    def list_works(self) -> list[Work]:
        with self._lock:
            rows = self._db.execute("SELECT * FROM works ORDER BY created").fetchall()
        return [_work(r) for r in rows]

    def find_work(self, *, source_url: Optional[str] = None,
                  hedgerow_url: Optional[str] = None) -> Optional[Work]:
        """An existing work by either of its URLs, without creating one."""
        with self._lock:
            row = None
            if source_url:
                row = self._db.execute(
                    "SELECT * FROM works WHERE source_url=?", (source_url,)
                ).fetchone()
            if row is None and hedgerow_url:
                row = self._db.execute(
                    "SELECT * FROM works WHERE hedgerow_url=?", (hedgerow_url,)
                ).fetchone()
        return _work(row) if row else None

    def delete_work(self, work_id: int) -> bool:
        """Forget a work entirely: its chapters and channel bindings go with it.

        `PRAGMA foreign_keys=ON` plus the ON DELETE CASCADE clauses in the schema are what
        make that one statement rather than three, and what stops a chapter surviving the
        work it belonged to. Nothing here reaches into Bindery: a book already filed in the
        library stays filed, because the library is not this plugin's to edit.
        """
        with self._lock:
            cur = self._db.execute("DELETE FROM works WHERE id=?", (work_id,))
            self._db.commit()
            return cur.rowcount > 0

    # ------------------------------------------------------------ bindings

    def bind_channel(self, channel_id: str, work_id: int) -> None:
        with self._lock:
            self._db.execute(
                "INSERT INTO channel_bindings(channel_id, work_id, bound_at) VALUES(?, ?, ?) "
                "ON CONFLICT(channel_id) DO UPDATE SET work_id=excluded.work_id, bound_at=excluded.bound_at",
                (channel_id, work_id, time.time()),
            )
            self._db.commit()

    def work_for_channel(self, channel_id: str) -> Optional[int]:
        with self._lock:
            row = self._db.execute(
                "SELECT work_id FROM channel_bindings WHERE channel_id=?", (channel_id,)
            ).fetchone()
        return int(row["work_id"]) if row else None

    def unbind_channel(self, channel_id: str) -> bool:
        """Stop watching a channel's forwarded EPUBs. Chapters already ingested stay."""
        with self._lock:
            cur = self._db.execute("DELETE FROM channel_bindings WHERE channel_id=?", (channel_id,))
            self._db.commit()
            return cur.rowcount > 0

    def bindings(self) -> list[dict]:
        with self._lock:
            rows = self._db.execute(
                "SELECT b.channel_id, b.work_id, b.bound_at, w.title "
                "FROM channel_bindings b JOIN works w ON w.id = b.work_id ORDER BY b.bound_at"
            ).fetchall()
        return [dict(r) for r in rows]

    # ------------------------------------------------------------ chapters

    def ingest_chapter(
        self,
        *,
        work_id: int,
        chapter_key: str,
        source: str,
        title: str,
        body_html: str,
        arc: Optional[int] = None,
        part: Optional[int] = None,
        published: Optional[str] = None,
        discord_ts: Optional[float] = None,
    ) -> bool:
        """Insert a chapter, keeping first-seen precedence.

        The `UNIQUE(work_id, chapter_key)` clause plus `ON CONFLICT DO NOTHING` is the whole
        of the first-seen rule: whichever source supplies a chapter first owns it, and a
        later supply of the same chapter — from the other source — is dropped, not merged
        over. Returns True when this call was the one that inserted.
        """
        with self._lock:
            cur = self._db.execute(
                "INSERT INTO chapters(work_id, chapter_key, source, title, arc, part, "
                "published, discord_ts, body_html, first_seen) "
                "VALUES(?, ?, ?, ?, ?, ?, ?, ?, ?, ?) "
                "ON CONFLICT(work_id, chapter_key) DO NOTHING",
                (work_id, chapter_key, source, title, arc, part, published, discord_ts,
                 body_html, time.time()),
            )
            self._db.commit()
            return cur.rowcount > 0

    def list_chapters(self, work_id: int) -> list[Chapter]:
        with self._lock:
            rows = self._db.execute(
                "SELECT * FROM chapters WHERE work_id=?", (work_id,)
            ).fetchall()
        return [_chapter(r) for r in rows]

    def chapter_body(self, chapter_id: int) -> str:
        with self._lock:
            row = self._db.execute(
                "SELECT body_html FROM chapters WHERE id=?", (chapter_id,)
            ).fetchone()
        return row["body_html"] if row else ""

    def reindex_chapters(self, key_of, number_of, *, version: str) -> int:
        """Recompute every stored chapter's key and number after a parser change.

        The `chapter_key` a chapter was stored under is whatever the parser said at the time,
        and improving the parser silently changes that answer. Without this, the next scrape
        would find none of its keys and re-ingest the whole back catalogue as duplicates —
        which is exactly what the UNIQUE constraint was meant to prevent.

        Runs once per parser version, guarded by a setting, and keeps the earliest-seen row
        when a smarter parser collapses two rows onto one key. Returns rows rewritten.
        """
        if self.get_setting(SETTING_KEY_VERSION) == version:
            return 0

        with self._lock:
            rows = self._db.execute(
                "SELECT id, work_id, chapter_key, title FROM chapters "
                "ORDER BY work_id, first_seen, id"
            ).fetchall()

            try:
                # Park every row on a temporary, globally unique key before assigning the
                # real ones. Rewriting in place collides with UNIQUE(work_id, chapter_key)
                # the moment one row's new key is a key another row has not yet given up.
                self._db.execute("UPDATE chapters SET chapter_key = '#' || id")

                seen: dict[tuple[int, str], int] = {}
                rewritten = 0

                for row in rows:
                    work_id, chapter_id = int(row["work_id"]), int(row["id"])
                    key = key_of(row["title"])
                    arc, part = number_of(row["title"])

                    if (work_id, key) in seen:
                        # First-seen precedence again, one level up: two rows the old parser
                        # told apart are now one chapter, and the older row is the one that
                        # owns it.
                        self._db.execute("DELETE FROM chapters WHERE id=?", (chapter_id,))
                        continue

                    seen[(work_id, key)] = chapter_id

                    if key != row["chapter_key"]:
                        rewritten += 1

                    self._db.execute(
                        "UPDATE chapters SET chapter_key=?, arc=?, part=? WHERE id=?",
                        (key, arc, part, chapter_id),
                    )

                self._db.execute(
                    "INSERT INTO settings(key, value) VALUES(?, ?) "
                    "ON CONFLICT(key) DO UPDATE SET value=excluded.value",
                    (SETTING_KEY_VERSION, version),
                )
                self._db.commit()
            except Exception:
                # All or nothing. Half-parked keys would be worse than the old ones, and the
                # guard setting is written in the same transaction so a failure retries next
                # boot rather than being silently marked done.
                self._db.rollback()
                raise

        return rewritten

    def set_manual_order(self, work_id: int, ordered_ids: Iterable[int]) -> None:
        with self._lock:
            for rank, chapter_id in enumerate(ordered_ids):
                self._db.execute(
                    "UPDATE chapters SET manual_order=? WHERE id=? AND work_id=?",
                    (rank, int(chapter_id), work_id),
                )
            self._db.commit()

    def set_pin(self, work_id: int, chapter_id: int, number: Optional[float]) -> None:
        with self._lock:
            self._db.execute(
                "UPDATE chapters SET pinned_number=? WHERE id=? AND work_id=?",
                (number, chapter_id, work_id),
            )
            self._db.commit()

    def delete_chapter(self, work_id: int, chapter_id: int) -> bool:
        """Drop one ingested chapter, so the source that supplies it next owns it again."""
        with self._lock:
            cur = self._db.execute(
                "DELETE FROM chapters WHERE id=? AND work_id=?", (chapter_id, work_id)
            )
            self._db.commit()
            return cur.rowcount > 0

    def clear_overrides(self, work_id: int) -> None:
        with self._lock:
            self._db.execute(
                "UPDATE chapters SET manual_order=NULL, pinned_number=NULL WHERE work_id=?",
                (work_id,),
            )
            self._db.commit()


def _work(row: sqlite3.Row) -> Work:
    return Work(
        id=int(row["id"]),
        source_url=row["source_url"],
        hedgerow_url=row["hedgerow_url"],
        source_id=row["source_id"],
        title=row["title"],
        author=row["author"],
    )


def _chapter(row: sqlite3.Row) -> Chapter:
    return Chapter(
        id=int(row["id"]),
        work_id=int(row["work_id"]),
        chapter_key=row["chapter_key"],
        source=row["source"],
        title=row["title"],
        arc=row["arc"],
        part=row["part"],
        published=row["published"],
        discord_ts=row["discord_ts"],
        pinned_number=row["pinned_number"],
        manual_order=row["manual_order"],
        first_seen=row["first_seen"],
    )
