"""Telling Bindery a source moved (protocol 3.7).

Everything else this plugin does happens because Bindery asked. This is the one thing it
says on its own, and only because Discord gives it information no poll could have found: the
author posts an advance copy at an arbitrary moment, and without a nudge the merged book
waits for the next scheduled sweep.

Three properties keep it from becoming a liability:

- **It carries nothing.** A source URL and a reason string. Chapters still travel the normal
  way, when Bindery comes to collect them.
- **It is best-effort.** Every failure is swallowed after a log line. The host's update
  schedule is the backstop, so a notification that never lands costs latency, never
  correctness — which is exactly why it is safe to never retry.
- **It is debounced.** A forwarded batch of five EPUBs is one notification, not five.
"""

from __future__ import annotations

import asyncio
import json
import logging
import urllib.error
import urllib.request
from typing import Optional

log = logging.getLogger("bindery.hedgerow")

SETTING_NOTIFY_URL = "notify_url"
SETTING_NOTIFY_TOKEN = "notify_token"

# Collapse a burst of arrivals into one call. Comfortably longer than the gap between
# messages in a batch of forwarded EPUBs, comfortably shorter than any update interval.
DEBOUNCE_SECONDS = 20.0

_TIMEOUT = 10.0


class Notifier:
    """Debounced, best-effort delivery of change hints.

    The endpoint and token are learned from response headers on requests Bindery makes, then
    persisted — the bot is not serving a request when a file arrives, so it cannot read them
    from anywhere else at that moment. They are re-learned on every request, which is what
    makes a host restart (and the new token it mints) invisible here.
    """

    def __init__(self, store):
        self._store = store
        self._pending: dict[str, asyncio.Task] = {}

    # ------------------------------------------------------------ configuration

    def remember(self, url: Optional[str], token: Optional[str]) -> None:
        """Record the endpoint Bindery advertised, if it changed."""
        url = (url or "").strip()
        token = (token or "").strip()

        if not url or not token:
            return

        if self._store.get_setting(SETTING_NOTIFY_URL) != url:
            self._store.set_setting(SETTING_NOTIFY_URL, url)
        if self._store.get_setting(SETTING_NOTIFY_TOKEN) != token:
            self._store.set_setting(SETTING_NOTIFY_TOKEN, token)

    def configured(self) -> bool:
        return bool(self._store.get_setting(SETTING_NOTIFY_URL)
                    and self._store.get_setting(SETTING_NOTIFY_TOKEN))

    # ------------------------------------------------------------ sending

    def schedule(self, source_url: str, reason: str = "new-chapters") -> None:
        """Queue a notification, collapsing repeats for the same source."""
        if not source_url or not self.configured():
            return

        existing = self._pending.get(source_url)
        if existing is not None and not existing.done():
            return  # a call for this source is already pending; it will cover this arrival

        try:
            loop = asyncio.get_running_loop()
        except RuntimeError:
            return  # no loop: nothing to schedule onto, and this is never worth raising for

        self._pending[source_url] = loop.create_task(
            self._after_debounce(source_url, reason), name="hedgerow-notify")

    async def _after_debounce(self, source_url: str, reason: str) -> None:
        try:
            await asyncio.sleep(DEBOUNCE_SECONDS)
            await asyncio.to_thread(self._post, source_url, reason)
        except asyncio.CancelledError:
            raise
        except Exception as exc:
            log.info("change notification not delivered (%s); the host's schedule will catch it",
                     type(exc).__name__)
        finally:
            self._pending.pop(source_url, None)

    def _post(self, source_url: str, reason: str) -> None:
        url = self._store.get_setting(SETTING_NOTIFY_URL)
        token = self._store.get_setting(SETTING_NOTIFY_TOKEN)

        if not url or not token:
            return

        body = json.dumps({
            "plugin": "hedgerow",
            "sourceUrl": source_url,
            "reason": reason,
        }).encode("utf-8")

        request = urllib.request.Request(url, data=body, method="POST")
        request.add_header("Content-Type", "application/json")
        request.add_header("Authorization", f"Bearer {token}")  # never logged

        try:
            with urllib.request.urlopen(request, timeout=_TIMEOUT) as response:
                payload = json.loads(response.read() or b"{}")
            accepted = response.status == 202 or bool(payload.get("accepted"))
            log.info("notified Bindery about %s: %s", source_url,
                     "queued" if accepted else f"declined ({payload.get('reason')})")
        except urllib.error.HTTPError as exc:
            # 401 means the host restarted and minted a new token; the next request Bindery
            # makes will re-advertise it, so there is nothing to do but drop this one.
            log.info("Bindery refused a change notification: HTTP %s", exc.code)
