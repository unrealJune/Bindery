"""Circulation conformance checks — docs/PLUGIN-PROTOCOL.md section 3.8.

Everything here is gated on `capabilities.circulation`. Checks that need an account are
gated on `--account` being supplied, and the two that have side effects at a real library —
placing a hold, and presenting a wrong PIN — are opt-in on top of that: a conformance run
must never get somebody's library card locked or put a book on their hold shelf by surprise.
"""

from __future__ import annotations

import re

from checks_protocol import CONFIG_KEY_RE, _check_fields, _notes_ignored_scope
from harness import Ctx, Fail, check, expect, expect_error_body, expect_status, expect_type

MEDIUMS = {"physical", "digital"}
HOLD_STATES = {"pending", "in_transit", "ready", "suspended"}
HOLD_OPERATIONS = {"cancel", "suspend", "resume"}
AVAILABILITY = {"available", "unavailable", "on_order", "not_holdable", "library_use_only", "unknown"}
TIMESTAMP_RE = re.compile(r"^\d{4}-\d{2}-\d{2}([T ]\d{2}:\d{2}.*)?$")


def _circulating(ctx: Ctx) -> bool:
    return bool(ctx.caps.get("circulation"))


def _with_account(ctx: Ctx) -> bool:
    return _circulating(ctx) and bool(ctx.args.account)


def _account(ctx: Ctx) -> dict:
    return {"id": "conformance-account", "fields": dict(ctx.args.account)}


def _call(ctx: Ctx, route: str, payload: dict, *, timeout: float = 35.0) -> dict:
    """POST a circulation call that is expected to succeed, returning its body."""
    resp = ctx.client.request("POST", f"/bindery/v1/circulation/{route}", json_body=payload, timeout=timeout)
    expect_status(resp, 200)
    expect(resp.content_type == "application/json",
           f"circulation/{route} must answer application/json, got {resp.content_type!r}")
    body = resp.json()
    expect_type(body, dict, f"circulation/{route} body")
    if body.get("status") == "error":
        raise Fail(f"circulation/{route} failed: {body.get('error')}")
    expect(body.get("status") == "ok", f"circulation/{route}: status must be ok|error, got {body.get('status')!r}")
    return body


def _secret_values(ctx: Ctx) -> list:
    secret_keys = {f["key"] for f in ctx.manifest.get("circulation", {}).get("accountFields", [])
                   if f.get("type") == "secret"}
    return [v for k, v in ctx.args.account.items() if k in secret_keys and len(v) >= 4]


def _optional_int(value, where: str) -> None:
    if value is not None:
        expect(isinstance(value, int) and not isinstance(value, bool) and value >= 0,
               f"{where} must be a non-negative integer, got {value!r}")


def _optional_timestamp(value, where: str) -> None:
    if value is not None:
        expect(isinstance(value, str) and TIMESTAMP_RE.match(value) is not None,
               f"{where} must be an RFC 3339 date or timestamp, got {value!r}")


def _check_common(entry: dict, where: str) -> None:
    expect_type(entry, dict, where)
    expect(isinstance(entry.get("id"), str) and entry["id"] != "", f"{where}: 'id' is required")
    expect(isinstance(entry.get("title"), str) and entry["title"].strip() != "", f"{where}: 'title' is required")
    expect(entry.get("medium") in MEDIUMS, f"{where}: 'medium' must be physical|digital, got {entry.get('medium')!r}")
    for author in entry.get("authors") or []:
        expect_type(author, str, f"{where}.authors entry")
    for key in ("url", "coverUrl"):
        if entry.get(key) is not None:
            expect(isinstance(entry[key], str) and entry[key].startswith("http"),
                   f"{where}.{key} must be an absolute URL")


def _check_item(item: dict, where: str) -> None:
    _check_common(item, where)
    expect(isinstance(item.get("holdable"), bool), f"{where}: 'holdable' must be a boolean")
    availability = item.get("availability")
    if availability is not None:
        expect_type(availability, dict, f"{where}.availability")
        expect(availability.get("status") in AVAILABILITY,
               f"{where}.availability.status must be one of {sorted(AVAILABILITY)}, got {availability.get('status')!r}")
        for key in ("copies", "availableCopies", "holds", "onOrder"):
            _optional_int(availability.get(key), f"{where}.availability.{key}")
    if item.get("borrowUrl") is not None:
        expect(isinstance(item["borrowUrl"], str) and item["borrowUrl"].startswith("https:"),
               f"{where}.borrowUrl must be an absolute https: URL")


def _check_hold(hold: dict, where: str) -> None:
    _check_common(hold, where)
    expect(hold.get("state") in HOLD_STATES,
           f"{where}.state must be one of {sorted(HOLD_STATES)}, got {hold.get('state')!r}")
    _optional_int(hold.get("queuePosition"), f"{where}.queuePosition")
    _optional_int(hold.get("queueSize"), f"{where}.queueSize")
    for key in ("readyUntil", "suspendedUntil", "placedAt"):
        _optional_timestamp(hold.get(key), f"{where}.{key}")
    location = hold.get("pickupLocation")
    if location is not None:
        expect(isinstance(location, dict) and isinstance(location.get("name"), str),
               f"{where}.pickupLocation must be an object with a 'name'")


def _check_loan(loan: dict, where: str) -> None:
    _check_common(loan, where)
    _optional_timestamp(loan.get("dueAt"), f"{where}.dueAt")
    for key in ("overdue", "renewable"):
        if loan.get(key) is not None:
            expect_type(loan[key], bool, f"{where}.{key}")


# ---------------------------------------------------------------- manifest


@check("circulation.manifest", group="circulation", requires=_circulating)
def circulation_manifest(ctx: Ctx) -> None:
    """The circulation block declares a valid account schema, mediums, and operations."""
    block = ctx.manifest.get("circulation")
    expect_type(block, dict, "circulation")
    fields = block.get("accountFields")
    expect(isinstance(fields, list) and fields, "circulation.accountFields must be a non-empty list")
    _check_fields(fields, "circulation.accountFields")
    _notes_ignored_scope(ctx, fields, "circulation.accountFields")
    mediums = block.get("mediums", sorted(MEDIUMS))
    expect(isinstance(mediums, list) and mediums and set(mediums) <= MEDIUMS,
           f"circulation.mediums must be a non-empty subset of {sorted(MEDIUMS)}")
    operations = block.get("holdOperations", ["cancel"])
    expect(isinstance(operations, list) and set(operations) <= HOLD_OPERATIONS,
           f"circulation.holdOperations must be a subset of {sorted(HOLD_OPERATIONS)}")
    for key in ctx.args.account:
        expect(CONFIG_KEY_RE.match(key) is not None, f"--account key {key!r} is not a valid field key")


@check("circulation.declines-downloads", group="circulation",
       requires=lambda ctx: _circulating(ctx) and not ctx.manifest.get("matches"))
def circulation_declines_downloads(ctx: Ctx) -> None:
    """A circulation-only plugin claims no URLs (it still serves download; see download.*)."""
    ctx.note("circulation-only plugin: 'download' is exercised by download.stream-shape above")


# ---------------------------------------------------------------- requests


@check("circulation.rejects-malformed", group="circulation", requires=_circulating)
def circulation_rejects_malformed(ctx: Ctx) -> None:
    """A circulation call with no 'account' is rejected with an error body."""
    for route in ("search", "snapshot"):
        resp = ctx.client.request("POST", f"/bindery/v1/circulation/{route}", json_body={"query": "x"}, timeout=15)
        expect_status(resp, 400, 422)
        expect_error_body(resp, f"malformed circulation/{route}")


@check("circulation.unknown-operation-is-404", group="circulation", requires=_circulating)
def circulation_unknown_operation(ctx: Ctx) -> None:
    """A hold operation outside the protocol is a 404."""
    resp = ctx.client.request(
        "POST", "/bindery/v1/circulation/holds/conformance-hold/obliterate",
        json_body={"account": {"id": "conformance-account", "fields": {}}}, timeout=15)
    expect_status(resp, 404)


@check("circulation.search", group="circulation", requires=_with_account)
def circulation_search(ctx: Ctx) -> None:
    """Search returns well-formed items carrying a medium and availability."""
    body = _call(ctx, "search", {"account": _account(ctx), "query": ctx.args.search_query, "medium": "any"})
    items = body.get("items")
    expect_type(items, list, "search.items")
    for key in ("total", "page", "pages"):
        _optional_int(body.get(key), f"search.{key}")
    for index, item in enumerate(items):
        _check_item(item, f"search.items[{index}]")
    if not items:
        ctx.note(f"search for {ctx.args.search_query!r} returned nothing; item shape went unchecked")


@check("circulation.search-medium-filter", group="circulation", requires=_with_account)
def circulation_search_medium(ctx: Ctx) -> None:
    """A medium filter is honoured: no physical item in a digital search, and vice versa."""
    declared = set(ctx.manifest.get("circulation", {}).get("mediums", sorted(MEDIUMS)))
    for medium in sorted(declared):
        body = _call(ctx, "search", {"account": _account(ctx), "query": ctx.args.search_query, "medium": medium})
        wrong = [i.get("title") for i in body.get("items", []) if i.get("medium") != medium]
        expect(not wrong, f"a {medium} search returned items of another medium: {wrong[:3]}")


@check("circulation.verify", group="circulation", requires=_with_account)
def circulation_verify(ctx: Ctx) -> None:
    """verify accepts the account and never echoes a secret back."""
    resp = ctx.client.request("POST", "/bindery/v1/circulation/verify",
                              json_body={"account": _account(ctx)}, timeout=35)
    for secret in _secret_values(ctx):
        expect(secret not in resp.text, "verify echoed a secret account field back in its response")
    body = _call(ctx, "verify", {"account": _account(ctx)})
    library = body.get("library")
    expect(isinstance(library, dict) and isinstance(library.get("name"), str),
           "verify must return library.name")
    for index, location in enumerate(body.get("pickupLocations") or []):
        expect(isinstance(location, dict) and isinstance(location.get("id"), str)
               and isinstance(location.get("name"), str),
               f"pickupLocations[{index}] needs string 'id' and 'name'")


@check("circulation.verify-refuses-bad-credentials", group="circulation",
       requires=lambda ctx: _with_account(ctx) and bool(ctx.args.try_bad_login))
def circulation_verify_refuses(ctx: Ctx) -> None:
    """verify answers auth_required for a wrong secret (opt-in: --try-bad-login)."""
    fields = dict(ctx.args.account)
    secrets = [f["key"] for f in ctx.manifest["circulation"]["accountFields"] if f.get("type") == "secret"]
    expect(secrets, "no secret account field to falsify")
    for key in secrets:
        fields[key] = "wrong"
    resp = ctx.client.request("POST", "/bindery/v1/circulation/verify",
                              json_body={"account": {"id": "conformance-bad", "fields": fields}}, timeout=35)
    expect_status(resp, 200)
    body = resp.json()
    expect(body.get("status") == "error" and (body.get("error") or {}).get("code") == "auth_required",
           f"a wrong secret must yield auth_required, got {body}")


@check("circulation.snapshot", group="circulation", requires=_with_account)
def circulation_snapshot(ctx: Ctx) -> None:
    """snapshot returns holds and loans in the protocol's states."""
    body = _call(ctx, "snapshot", {"account": _account(ctx)})
    holds, loans = body.get("holds"), body.get("loans")
    expect_type(holds, list, "snapshot.holds")
    expect_type(loans, list, "snapshot.loans")
    for index, hold in enumerate(holds):
        _check_hold(hold, f"snapshot.holds[{index}]")
    for index, loan in enumerate(loans):
        _check_loan(loan, f"snapshot.loans[{index}]")
    ids = [h["id"] for h in holds] + [entry["id"] for entry in loans]
    expect(len(ids) == len(set(ids)), "hold and loan ids must be unique within a snapshot")


@check("circulation.hold-round-trip", group="circulation",
       requires=lambda ctx: _with_account(ctx) and bool(ctx.args.hold_item))
def circulation_hold_round_trip(ctx: Ctx) -> None:
    """Place a hold, find it in the snapshot, cancel it (opt-in: --hold-item)."""
    placed = _call(ctx, "holds", {"account": _account(ctx), "itemId": ctx.args.hold_item})
    if placed.get("hold") is not None:
        _check_hold(placed["hold"], "holds.hold")
    snapshot = _call(ctx, "snapshot", {"account": _account(ctx)})
    match = next((h for h in snapshot.get("holds", []) if h.get("itemId") == ctx.args.hold_item), None)
    expect(match is not None, f"a hold on {ctx.args.hold_item!r} was placed but is not in the snapshot")
    if "cancel" in ctx.manifest["circulation"].get("holdOperations", ["cancel"]):
        _call(ctx, f"holds/{match['id']}/cancel", {"account": _account(ctx)})
        after = _call(ctx, "snapshot", {"account": _account(ctx)})
        expect(all(h.get("id") != match["id"] for h in after.get("holds", [])),
               "a cancelled hold is still in the snapshot")
    else:
        ctx.note(f"plugin cannot cancel; the test hold {match['id']} is still on the account")
