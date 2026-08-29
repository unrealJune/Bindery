"""The Discord side: a bot that turns forwarded advance-copy EPUBs into ingested chapters.

It runs as an asyncio task inside the plugin's own event loop — not a second process — and
it is entirely optional. With no token configured the manager stays dormant, `/healthz`
still answers, and Royal Road downloads work exactly as before. That graceful-degradation
property is a hard requirement, so every entry point here tolerates a missing library and a
missing token.

Two Discord specifics drive the shape of this file:

- **Forwarded messages** carry their real payload under `message_snapshots[]`, each snapshot
  with its own attachments. Both the message's own attachments and every snapshot's are
  checked, or forwarded EPUBs would be silently missed.
- **CDN attachment URLs are signed and expire** (~24h). The bytes are read on receipt and
  stored in the durable DB; a CDN URL is never persisted in the hope it still resolves later.
"""

from __future__ import annotations

import asyncio
import functools
import logging
import os
import re
import tempfile
from typing import List, Optional, Set

from . import epub, merge, royalroad
from .store import DATA_DIR, SOURCE_DISCORD, Store

log = logging.getLogger("bindery.hedgerow")

SETTING_TOKEN = "discord_token"
SETTING_CHANNELS = "discord_channels"

_RR_URL = re.compile(r"https?://(?:www\.)?royalroad\.com/fiction/\d+[^\s>]*", re.I)


@functools.lru_cache(maxsize=256)
def _work_url(url: str) -> str:
    """Key a work the way the download path keys it.

    `main._resolve_work` always runs a Royal Road URL through `royalroad.normalize` before
    `get_or_create_work`, and that lookup matches on exact string equality. A URL as pasted
    into Discord — bare `http://`, no `www.`, a trailing slug or none — is a different
    string from the normalized one, so keying works off the raw text would bind the channel
    to a work that no download or rescan can ever reach: Discord chapters would collect on
    one work and Royal Road chapters on its twin, and the merge this plugin exists to do
    would never happen. Normalizing on both sides is what keeps them the same work.

    Cached because `normalize` loads FanFicFare's adapter table, which is far too heavy to
    repeat for every message in a chatty channel.
    """
    normalized = royalroad.normalize(url)
    return normalized[0] if normalized else url


class BotManager:
    """Owns the bot's lifecycle and reflects config changes without a restart.

    Config reaches a plugin per request, but a chat bot has to run continuously, so the last
    token and channel list are persisted in the store and reloaded at boot. Applying a new
    token tears the old client down and starts a fresh one; clearing it stops the bot.
    """

    def __init__(self, store: Store):
        self._store = store
        self._token: Optional[str] = None
        self._channels: Set[str] = set()
        self._task: Optional[asyncio.Task] = None
        self._client = None
        self._status = "disabled"
        self._detail = "no bot token configured"
        self._user: Optional[str] = None

    # ------------------------------------------------------------ lifecycle

    async def start_from_persisted(self) -> None:
        """At boot, revive whatever was last configured — persisted value or env fallback."""
        token = self._store.get_setting(SETTING_TOKEN) or os.environ.get("HEDGEROW_DISCORD_TOKEN")
        channels = self._store.get_setting(SETTING_CHANNELS) or os.environ.get("HEDGEROW_DISCORD_CHANNELS", "")
        await self.apply_config(token, _split_channels(channels), persist=False)

    async def apply_config(self, token: Optional[str], channels, *, persist: bool = True) -> None:
        token = (token or "").strip() or None
        channel_set = {c.strip() for c in channels if str(c).strip()} if channels else set()

        if persist:
            self._store.set_setting(SETTING_TOKEN, token or "")
            self._store.set_setting(SETTING_CHANNELS, ",".join(sorted(channel_set)))

        # Channels can change hot; a token change means a new session.
        self._channels = channel_set
        if token == self._token and self._task and not self._task.done():
            return
        self._token = token
        await self._restart()

    async def _restart(self) -> None:
        await self.stop()
        if not self._token:
            self._status, self._detail = "disabled", "no bot token configured"
            return
        try:
            import discord  # noqa: F401  — imported lazily so a tokenless plugin needs no dep
        except Exception as exc:
            self._status, self._detail = "error", f"discord.py not installed: {exc}"
            log.warning("Discord bot requested but discord.py is unavailable: %s", exc)
            return
        self._status, self._detail = "connecting", "starting Discord client"
        self._task = asyncio.create_task(self._run(), name="hedgerow-discord")

    async def stop(self) -> None:
        if self._client is not None:
            with _suppress():
                await self._client.close()
            self._client = None
        if self._task is not None:
            self._task.cancel()
            with _suppress(asyncio.CancelledError):
                await self._task
            self._task = None

    # ------------------------------------------------------------ the bot

    async def _run(self) -> None:
        import discord

        intents = discord.Intents.default()
        intents.message_content = True  # privileged: required to read posted Royal Road URLs
        client = discord.Client(intents=intents)
        self._client = client

        @client.event
        async def on_ready():
            self._status = "connected"
            self._user = str(client.user)
            self._detail = f"connected as {self._user}"
            log.info("Discord bot connected as %s", self._user)

        @client.event
        async def on_message(message):
            with _suppress():
                await self._handle(message)

        try:
            await client.start(self._token)  # type: ignore[arg-type]
        except asyncio.CancelledError:
            raise
        except Exception as exc:
            # Never leak the token, only the failure class.
            self._status, self._detail = "error", f"{type(exc).__name__}"
            log.warning("Discord bot stopped: %s", type(exc).__name__)

    async def _handle(self, message) -> None:
        channel_id = str(getattr(message.channel, "id", ""))
        if self._channels and channel_id not in self._channels:
            return

        # A posted Royal Road fiction URL (re)binds this channel's current work.
        match = _RR_URL.search(message.content or "")
        if match:
            work = self._store.get_or_create_work(source_url=_work_url(match.group(0)))
            self._store.bind_channel(channel_id, work.id)
            log.info("channel %s bound to work %s", channel_id, work.id)

        attachments = _all_attachments(message)
        if not attachments:
            return

        work_id = self._store.work_for_channel(channel_id)
        if work_id is None:
            log.info("channel %s has EPUBs but no bound work; ignoring until a URL is posted", channel_id)
            return

        ts = _timestamp(message)
        for attachment in attachments:
            if not str(getattr(attachment, "filename", "")).lower().endswith(".epub"):
                continue
            try:
                data = await attachment.read()  # bytes now — the CDN URL will expire
            except Exception as exc:
                log.warning("could not read attachment: %s", type(exc).__name__)
                continue
            self._ingest_epub(work_id, data, ts)

    def _ingest_epub(self, work_id: int, data: bytes, discord_ts: float) -> None:
        tmp_dir = os.path.join(DATA_DIR, "incoming")
        os.makedirs(tmp_dir, exist_ok=True)
        added = 0
        with tempfile.NamedTemporaryFile(dir=tmp_dir, suffix=".epub", delete=False) as handle:
            handle.write(data)
            path = handle.name
        try:
            for chapter in epub.read_chapters(path):
                arc, part = merge.parse_number(chapter.title)
                if self._store.ingest_chapter(
                    work_id=work_id,
                    chapter_key=merge.chapter_key(chapter.title),
                    source=SOURCE_DISCORD,
                    title=chapter.title,
                    body_html=chapter.body_html,
                    arc=arc, part=part, discord_ts=discord_ts,
                ):
                    added += 1
        finally:
            with _suppress():
                os.unlink(path)
        if added:
            log.info("ingested %d new chapter(s) from a Discord EPUB into work %s", added, work_id)

    # ------------------------------------------------------------ introspection

    def status(self) -> dict:
        return {
            "status": self._status,
            "detail": self._detail,
            "user": self._user,
            "channels": sorted(self._channels),
        }


def _all_attachments(message) -> List:
    """Own attachments plus every forwarded snapshot's — forwarded EPUBs live only here."""
    found = list(getattr(message, "attachments", None) or [])
    for snapshot in getattr(message, "message_snapshots", None) or []:
        found.extend(getattr(snapshot, "attachments", None) or [])
        inner = getattr(snapshot, "message", None)
        if inner is not None:
            found.extend(getattr(inner, "attachments", None) or [])
    return found


def _timestamp(message) -> float:
    created = getattr(message, "created_at", None)
    try:
        return created.timestamp() if created else 0.0
    except Exception:
        return 0.0


def _split_channels(value: str) -> List[str]:
    return [part.strip() for part in re.split(r"[,\s]+", value or "") if part.strip()]


class _suppress:
    """contextlib.suppress with a default of 'everything', for best-effort cleanup."""

    def __init__(self, *exceptions):
        self._exceptions = exceptions or (Exception,)

    def __enter__(self):
        return self

    def __exit__(self, exc_type, exc, tb):
        return exc_type is not None and issubclass(exc_type, self._exceptions)
