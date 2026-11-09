"""Signing in to a BiblioCommons account, and reading and changing its holds and loans.

**Read this before changing anything here.** The catalog side of this plugin (``catalog.py``)
was built against live responses. This side could not be: it needs a real library card, and
no credentials were used while writing it. What *is* verified:

- the login form: ``POST {catalog}/user/login`` with ``name``, ``user_pin``, ``local``, and a
  Rails ``authenticity_token`` scraped from the same page;
- that ``GET {gateway}/holds?accountId=…`` and ``GET {gateway}/checkouts?accountId=…`` exist
  and answer ``401 invalid_token`` without a session.

Verified end to end against a live Seattle Public Library account (Nov 2026): sign-in, the
account id (``STATE_ACCOUNT_RE``: it is *not* the user id), the opaque ``bc_access_token`` /
``session_id`` cookies accepted as ``X-Access-Token`` / ``X-Session-Id``, and the holds and
checkouts listings. Not yet exercised live: loans (the test account had none) and the
place / cancel / suspend writes.

What is still *reconstructed* from how the BiblioCommons web app behaves, and isolated here so it
can be corrected in one place against a real account:

- the session is carried by the ``bc_access_token`` and ``session_id`` cookies, which the
  gateway also accepts as ``X-Access-Token`` / ``X-Session-Id`` headers;
- the account id appears in the signed-in dashboard as ``"accountId":NNN`` (or in the
  access token's claims);
- the shapes of the holds and checkouts entities, and of the place/cancel bodies.

Every read below therefore tries several plausible keys and falls back conservatively, and
anything it cannot make sense of becomes a protocol error rather than a guess: a snapshot is
complete or it is an error (protocol §3.8), because a misread hold that silently drops out
would be reported to the person as cancelled.
"""

from __future__ import annotations

import base64
import hashlib
import json
import logging
import re
import threading
import time
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Any, Dict, List, Optional, Tuple
from urllib.parse import quote

from . import catalog
from .libraries import Library
from .web import Browser, SiteError

log = logging.getLogger("bindery.bibliocommons")

SESSION_TTL = 20 * 60
PAGE_SIZE = 50
MAX_PAGES = 20

# Attribute order and quoting vary; match the whole <input> and pull the value out of it.
TOKEN_INPUT_RE = re.compile(r'<input\b[^>]*\bname=["\']authenticity_token["\'][^>]*>')
TOKEN_VALUE_RE = re.compile(r'\bvalue=["\']([^"\']+)["\']')
HTML_HEADERS = {"Accept": "text/html"}


def find_token(page: str) -> Optional[str]:
    tag = TOKEN_INPUT_RE.search(page)
    value = TOKEN_VALUE_RE.search(tag.group(0)) if tag else None
    return value.group(1) if value else None
# The /v2/ pages embed the redux state. Verified against live Seattle and KCLS pages:
#
# - the user id and the *account* id are different numbers, and the gateway answers 500 if
#   given anything but a real account id;
# - one BiblioCommons *user* can hold accounts at several libraries (a friend's KCLS card
#   attached to a user whose home library is Seattle), so "the user's first account" is
#   the wrong library about as often as it is the right one. Each entry of the state's
#   ``accounts`` table carries the card's ``barcode`` and the library's ``libId``; the card
#   that was just used to sign in is the one to pick;
# - a bare ``"accountId":"NNNN"`` also appears on the page, but it is the Syndetics cover-art
#   widget's configuration, not the patron's. Never match on that key.
#
# Access-token cookies are opaque (not JWTs), so there is no claim to read it from.
STATE_ACCOUNTS_RE = re.compile(r'"accounts"\s*:\s*(?=\{\s*"\d+"\s*:\s*\{)')

# BiblioCommons hold statuses → protocol hold states.
HOLD_STATES = {
    "NOT_YET_AVAILABLE": "pending",
    "PENDING": "pending",
    "ACTIVE": "pending",
    "IN_TRANSIT": "in_transit",
    "SHIPPED": "in_transit",
    "READY_FOR_PICKUP": "ready",
    "READY": "ready",
    "AVAILABLE": "ready",
    "SUSPENDED": "suspended",
    "FROZEN": "suspended",
    "PAUSED": "suspended",
    "INACTIVE": "suspended",
}


@dataclass
class Session:
    library: Library
    browser: Browser
    account_id: str
    fingerprint: str
    created: float = field(default_factory=time.monotonic)

    @property
    def fresh(self) -> bool:
        return time.monotonic() - self.created < SESSION_TTL

    def headers(self) -> dict:
        headers = {"Accept": "application/json"}
        token = self.browser.cookie("bc_access_token")
        session = self.browser.cookie("session_id")
        if token:
            headers["X-Access-Token"] = token
        if session:
            headers["X-Session-Id"] = session
        return headers


class Sessions:
    """Signed-in sessions, in memory only, keyed by the host's opaque account id.

    The fingerprint is a hash of the credentials, so a session is dropped the moment the
    person changes their PIN in Bindery (protocol §3.8: discard on changed fields). Nothing
    here touches disk: a restart signs everyone in again, which is the right price for never
    holding a library session at rest.
    """

    def __init__(self) -> None:
        self._sessions: Dict[str, Session] = {}
        self._lock = threading.Lock()

    def get(self, account_id: str, library: Library, card: str, pin: str, *, fresh: bool = False) -> Session:
        fingerprint = hashlib.sha256(f"{library.slug}\0{card}\0{pin}".encode()).hexdigest()
        with self._lock:
            existing = self._sessions.get(account_id)
        if existing and not fresh and existing.fresh and existing.fingerprint == fingerprint:
            return existing
        session = login(library, card, pin, fingerprint)
        with self._lock:
            self._sessions[account_id] = session
        return session

    def forget(self, account_id: str) -> None:
        with self._lock:
            self._sessions.pop(account_id, None)


def credentials(fields: dict) -> Tuple[str, str]:
    card = re.sub(r"\s+", "", str(fields.get("card") or ""))
    pin = str(fields.get("pin") or "")
    if not card or not pin:
        raise SiteError("auth_required", "a card number and PIN are both required")
    return card, pin


def login(library: Library, card: str, pin: str, fingerprint: str) -> Session:
    browser = Browser()
    url = f"{library.catalog}/user/login?destination=%2Fuser_dashboard"

    # Ask for HTML explicitly: with the client's default Accept (JSON first) the site answers
    # with a JSON modal fragment whose markup is escaped and carries no form token.
    page = browser.request("GET", url, headers=HTML_HEADERS)
    token = find_token(page.text)
    if not token:
        raise SiteError("parse", "the library's sign-in page has changed; no form token was found")

    reply = browser.request("POST", url, form={
        "utf8": "✓",
        "authenticity_token": token,
        "name": card,
        "user_pin": pin,
        "local": "false",
    }, headers=HTML_HEADERS, ok=(200,))

    # A failed sign-in renders the form again; a successful one lands on the dashboard and
    # sets a session cookie. Either signal alone is accepted as success, both absent is not.
    signed_in = browser.cookie("bc_access_token") or browser.cookie("session_id")
    if not signed_in or "/user/login" in reply.url and 'name="user_pin"' in reply.text:
        raise SiteError("auth_required", "the library did not accept that card number and PIN")

    account_id = _account_id(browser, reply.text, card)
    if not account_id:
        # The legacy dashboard carries no account; the /v2/ pages embed the state that does.
        state = browser.request("GET", f"{library.catalog}/v2/holds", headers=HTML_HEADERS)
        account_id = _account_id(browser, state.text, card)
    if not account_id:
        raise SiteError("parse", "signed in, but could not find this card's account id on the dashboard")

    log.info("signed in to %s", library.slug)  # never the card number
    return Session(library=library, browser=browser, account_id=account_id, fingerprint=fingerprint)


def _account_id(browser: Browser, dashboard: str, card: str) -> Optional[str]:
    """The id of the account that belongs to ``card``, or None. Never a guess."""
    accounts = _state_accounts(dashboard)
    for account_id, entry in accounts.items():
        if re.sub(r"\s+", "", str(entry.get("barcode") or "")) == card:
            return account_id
    # A single account needs no matching (the barcode may be formatted differently); several
    # that none of match is ambiguous, and the wrong one reads someone else's library.
    if len(accounts) == 1:
        return next(iter(accounts))
    if accounts:
        return None

    token = browser.cookie("bc_access_token")
    if token and token.count(".") == 2:
        try:
            payload = token.split(".")[1]
            claims = json.loads(base64.urlsafe_b64decode(payload + "=" * (-len(payload) % 4)))
            for key in ("accountId", "account_id", "patronId"):
                if claims.get(key):
                    return str(claims[key])
        except (ValueError, json.JSONDecodeError):
            pass
    return None


def _state_accounts(page: str) -> Dict[str, dict]:
    """The ``accounts`` table of the redux state embedded in a /v2/ page, keyed by account id."""
    decoder = json.JSONDecoder()
    for match in STATE_ACCOUNTS_RE.finditer(page):
        try:
            table, _ = decoder.raw_decode(page, match.end())
        except json.JSONDecodeError:
            continue
        if isinstance(table, dict):
            found = {str(k): v for k, v in table.items() if isinstance(v, dict)}
            if found:
                return found
    return {}


# ---------------------------------------------------------------- reading the account


def snapshot(session: Session) -> dict:
    holds, hold_bibs = _collect(session, "holds")
    loans, loan_bibs = _collect(session, "checkouts")
    bibs = {**hold_bibs, **loan_bibs}
    return {
        "status": "ok",
        "holds": [_hold(session.library, entry, bibs) for entry in holds],
        "loans": [_loan(session.library, entry, bibs) for entry in loans],
    }


def _collect(session: Session, kind: str) -> Tuple[List[dict], Dict[str, dict]]:
    """Every entry of one kind, across every page — or an exception. Never a partial list."""
    entries: List[dict] = []
    bibs: Dict[str, dict] = {}
    for page in range(1, MAX_PAGES + 1):
        url = (f"{session.library.gateway}/{kind}?accountId={quote(session.account_id)}"
               f"&page={page}&size={PAGE_SIZE}&locale=en-US")
        body = session.browser.request("GET", url, headers=session.headers()).json()
        if not isinstance(body, dict):
            raise SiteError("parse", f"the library's {kind} list was not an object")

        entities = body.get("entities") or {}
        table = entities.get(kind) or entities.get(kind.rstrip("s")) or {}
        if not isinstance(table, dict):
            raise SiteError("parse", f"the library's {kind} list has an unexpected shape")
        bibs.update({k: v for k, v in (entities.get("bibs") or {}).items() if isinstance(v, dict)})

        # Live gateway: ``borrowing.<kind>.{items, pagination}``. The top-level ``<kind>.results``
        # shape is kept for older responses. Reading only the latter lost the pagination, so a
        # card with more than one page would have been reported as complete after the first.
        listing = (body.get("borrowing") or {}).get(kind) or body.get(kind) or {}
        order = (listing.get("items") or listing.get("results")) if isinstance(listing, dict) else None
        ids = [str(i) for i in order] if isinstance(order, list) else list(table.keys())
        for entry_id in ids:
            entry = table.get(entry_id)
            if not isinstance(entry, dict):
                raise SiteError("parse", f"the library listed a {kind[:-1]} it did not describe")
            entries.append({**entry, "_id": entry_id})

        pagination = (listing.get("pagination") if isinstance(listing, dict) else None) or body.get("pagination") or {}
        pages = pagination.get("pages") if isinstance(pagination, dict) else None
        if not isinstance(pages, int) or page >= pages:
            return entries, bibs
    raise SiteError("parse", f"more than {MAX_PAGES} pages of {kind}; refusing a partial list")


def _first(entry: dict, *keys: str) -> Any:
    for key in keys:
        value = entry.get(key)
        if value not in (None, "", []):
            return value
    return None


def _date(value: Any) -> Optional[str]:
    """RFC 3339 for a timestamp, or the bare date libraries usually give."""
    if value in (None, ""):
        return None
    text = str(value)
    if re.fullmatch(r"\d{4}-\d{2}-\d{2}", text):
        return text
    try:
        parsed = datetime.fromisoformat(text.replace("Z", "+00:00"))
    except ValueError:
        return None
    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=timezone.utc)
    return parsed.astimezone(timezone.utc).isoformat().replace("+00:00", "Z")


def _int(value: Any) -> Optional[int]:
    try:
        number = int(value)
    except (TypeError, ValueError):
        return None
    return number if number >= 0 else None


def _common(library: Library, entry: dict, bibs: Dict[str, dict]) -> dict:
    metadata_id = _first(entry, "metadataId", "bibId", "itemId")
    bib = bibs.get(str(metadata_id)) if metadata_id else None
    item = catalog.item_from_bib(library, bib) if bib else None
    material = str(_first(entry, "materialType", "bibType") or "").upper()
    medium = item["medium"] if item else ("digital" if material == "DIGITAL" else "physical")
    title = (item or {}).get("title") or _first(entry, "title", "bibTitle")
    if not title:
        raise SiteError("parse", "the library listed an item without a title")
    return {
        "itemId": str(metadata_id) if metadata_id else None,
        "title": str(title),
        "authors": (item or {}).get("authors") or [],
        "coverUrl": (item or {}).get("coverUrl"),
        "url": (item or {}).get("url") or (library.record_url(str(metadata_id)) if metadata_id else None),
        "medium": medium,
        "format": (item or {}).get("format"),
    }


def _hold(library: Library, entry: dict, bibs: Dict[str, dict]) -> dict:
    raw = str(_first(entry, "status", "holdStatus") or "").upper()
    state = HOLD_STATES.get(raw)
    if state is None:
        # The protocol allows only its four states. A status this plugin does not know is
        # reported as still queued — the one reading that never tells someone a book is
        # waiting when it is not — and the library's own word travels alongside for the log.
        log.warning("unrecognized BiblioCommons hold status %r; reporting it as pending", raw)
        state = "pending"
    location = _first(entry, "pickupLocation", "pickupBranch")
    if isinstance(location, dict):
        location = {"id": str(location.get("code") or location.get("id") or ""), "name": str(location.get("name") or "")}
        location = location if location["name"] else None
    elif isinstance(location, str):
        location = {"id": location, "name": location}
    return {
        "id": str(_first(entry, "holdsId", "holdId", "id") or entry["_id"]),
        **_common(library, entry, bibs),
        "state": state,
        "libraryStatus": raw or None,
        "queuePosition": _int(_first(entry, "holdsPosition", "queuePosition", "position")) if state == "pending" else None,
        "queueSize": _int(_first(entry, "holdsCount", "queueSize", "totalHolds")) if state == "pending" else None,
        "pickupLocation": location,
        "readyUntil": _date(_first(entry, "pickupByDate", "pickupExpiryDate", "holdExpiryDate")) if state == "ready" else None,
        "suspendedUntil": _date(_first(entry, "suspendEndDate", "suspendedUntil")) if state == "suspended" else None,
        "placedAt": _date(_first(entry, "createdDate", "placedDate", "holdPlacedDate")),
    }


def _loan(library: Library, entry: dict, bibs: Dict[str, dict]) -> dict:
    status = str(_first(entry, "status") or "").upper()
    renewable = _first(entry, "isRenewable", "renewable")
    return {
        "id": str(_first(entry, "checkoutId", "id") or entry["_id"]),
        **_common(library, entry, bibs),
        "dueAt": _date(_first(entry, "dueDate", "dueAt")),
        "overdue": status == "OVERDUE",
        "renewable": bool(renewable) if isinstance(renewable, bool) else None,
    }


# ---------------------------------------------------------------- changing the account


def place_hold(session: Session, item_id: str, pickup_location_id: Optional[str]) -> None:
    body: dict = {
        "metadataId": item_id,
        "materialType": "PHYSICAL",
        "accountId": int(session.account_id) if session.account_id.isdigit() else session.account_id,
        "enableSingleClickHolds": False,
    }
    if pickup_location_id:
        body["pickupLocation"] = pickup_location_id
    reply = session.browser.request(
        "POST", f"{session.library.gateway}/holds?locale=en-US", json_body=body, headers=session.headers(),
        ok=(200, 201, 400, 409, 422))
    if reply.status in (400, 409, 422):
        raise _hold_refusal(reply)


def cancel_hold(session: Session, hold_id: str) -> None:
    session.browser.request(
        "DELETE", f"{session.library.gateway}/holds?locale=en-US",
        json_body={"holdIds": [hold_id], "accountId": session.account_id},
        headers=session.headers(), ok=(200, 202, 204))


def _hold_refusal(reply) -> SiteError:
    """Turns the library's refusal into one of the protocol's circulation codes."""
    text = reply.text.lower()
    if "already" in text and "hold" in text:
        return SiteError("already_held", "you already have a hold on this", retryable=False)
    if "limit" in text or "maximum" in text:
        return SiteError("hold_limit", "this card has reached the library's hold limit", retryable=False)
    if "block" in text or "fine" in text or "expired" in text:
        return SiteError("account_blocked", "the library has blocked holds on this card", retryable=False)
    if "not holdable" in text or "cannot be placed" in text or "not available for hold" in text:
        return SiteError("not_holdable", "the library does not take holds on this item", retryable=False)
    return SiteError("internal", f"the library refused the hold ({reply.status})", retryable=False)
