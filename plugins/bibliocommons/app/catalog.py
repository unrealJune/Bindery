"""Mapping BiblioCommons catalog records onto protocol §3.8 items.

Pure functions over the gateway's JSON — no network — so the part of this plugin that decides
"physical or digital?" is unit tested against responses captured from the real SPL and KCLS
gateways (``tests/fixtures/``).

The shapes here are verified. The search endpoint is public, and every key read below was
observed in live responses from both libraries in November 2026.
"""

from __future__ import annotations

from typing import Iterable, List, Optional
from urllib.parse import urlparse

from .libraries import Library

# BiblioCommons format codes → (protocol format, label). Anything unlisted keeps the
# library's own code as its label and maps to "other".
FORMATS = {
    "BK": ("book", "Book"),
    "LPRINT": ("large_print", "Large print"),
    "LARGEPRINT": ("large_print", "Large print"),
    "GRAPHIC_NOVEL": ("graphic_novel", "Graphic novel"),
    "BOARD_BK": ("book", "Board book"),
    "PICTURE_BK": ("book", "Picture book"),
    "EBOOK": ("ebook", "eBook"),
    "GRAPHIC_NOVEL_DOWNLOAD": ("ebook", "Digital comic"),
    "AB": ("audiobook", "eAudiobook"),
    "EAUDIOBOOK": ("audiobook", "eAudiobook"),
    "BOOK_CD": ("audiobook_cd", "Audiobook CD"),
    "MP3_CD": ("audiobook_cd", "MP3 audiobook CD"),
    "DVD": ("dvd", "DVD"),
    "BLURAY": ("bluray", "Blu-ray"),
    "MUSIC_CD": ("music_cd", "Music CD"),
    "MAG": ("magazine", "Magazine"),
    "EMAG": ("magazine", "eMagazine"),
}

PHYSICAL_FILTER = "_physical_"
DIGITAL_FILTER = "_online_"

# Branch facet entries that are places books live, not places people collect them.
NOT_PICKUP = ("digital", "online", "storage", "services", "resources", "technical")


def medium_of(bib: dict) -> str:
    """'digital' or 'physical'.

    ``policy.materialType`` is the primary signal, but not the only one: KCLS lists hoopla
    music as ``PHYSICAL`` with provider ``ILS`` and an ``eresourceUrl``. Anything electronic
    by super-format or with an e-resource link is digital, because telling someone to drive
    to a branch for a streaming album is the worse mistake.
    """
    policy = bib.get("policy") or {}
    brief = bib.get("briefInfo") or {}
    availability = bib.get("availability") or {}
    if str(policy.get("materialType") or "").upper() == "DIGITAL":
        return "digital"
    if "ELECTRONIC_FORMATS" in (brief.get("superFormats") or []):
        return "digital"
    if availability.get("eresourceUrl"):
        return "digital"
    return "physical"


def provider_of(bib: dict) -> Optional[str]:
    """The lending platform for a digital item, as a person would name it."""
    policy = bib.get("policy") or {}
    raw = str(policy.get("provider") or "")
    if raw.lower().startswith("overdrive"):
        return "OverDrive (Libby)"
    eresource = (bib.get("availability") or {}).get("eresourceUrl")
    if eresource:
        host = (urlparse(eresource).hostname or "").lower()
        if "hoopla" in host:
            return "hoopla"
        if "cloudlibrary" in host or "yourcloudlibrary" in host:
            return "cloudLibrary"
        if "kanopy" in host:
            return "Kanopy"
        return host.removeprefix("www.") or None
    if raw and raw.upper() != "ILS":
        return raw.removesuffix("API")
    return None


def availability_of(bib: dict) -> dict:
    a = bib.get("availability") or {}
    status = str(a.get("status") or "").upper()
    circulation = str(a.get("circulationType") or "").upper()
    localised = str(a.get("localisedStatus") or "").upper()

    if a.get("libraryUseOnly"):
        mapped = "library_use_only"
    elif circulation == "NON_CIRCULATING" or "NOT_HOLDABLE" in localised:
        mapped = "not_holdable"
    elif status == "AVAILABLE":
        mapped = "available"
    elif status in ("ON_ORDER", "ONORDER") or (a.get("onOrderCopies") or 0) > 0 and not a.get("totalCopies"):
        mapped = "on_order"
    elif status == "UNAVAILABLE":
        mapped = "unavailable"
    else:
        mapped = "unknown"

    def count(key: str) -> Optional[int]:
        value = a.get(key)
        return value if isinstance(value, int) and not isinstance(value, bool) and value >= 0 else None

    return {
        "status": mapped,
        "copies": count("totalCopies"),
        "availableCopies": count("availableCopies"),
        "holds": count("heldCopies"),
        "onOrder": count("onOrderCopies"),
    }


def cover_of(bib: dict) -> Optional[str]:
    jacket = (bib.get("briefInfo") or {}).get("jacket") or {}
    for size in ("medium", "large", "small"):
        url = jacket.get(size)
        if isinstance(url, str) and url.startswith("https://"):
            return url
    return None


def item_from_bib(library: Library, bib: dict) -> Optional[dict]:
    """One protocol catalog item, or None for a record without an id or title."""
    brief = bib.get("briefInfo") or {}
    policy = bib.get("policy") or {}
    metadata_id = bib.get("id") or brief.get("metadataId")
    title = brief.get("title")
    if not metadata_id or not title:
        return None

    medium = medium_of(bib)
    code = str(brief.get("format") or "")
    fmt, label = FORMATS.get(code, ("other", code.replace("_", " ").title() or "Item"))
    availability = availability_of(bib)
    record = library.record_url(str(metadata_id))

    # Physical holds are placed through the ILS, which this plugin speaks to. Digital holds
    # belong to the lending platform (OverDrive's own account system), so a digital item is
    # never offered as holdable here: it links out to where it can actually be borrowed.
    holdable = (
        medium == "physical"
        and bool(policy.get("holdable"))
        and availability["status"] not in ("not_holdable", "library_use_only")
    )

    borrow_url = None
    if medium == "digital":
        eresource = (bib.get("availability") or {}).get("eresourceUrl")
        borrow_url = eresource if isinstance(eresource, str) and eresource.startswith("https://") else record

    return {
        "id": str(metadata_id),
        "title": str(title),
        "subtitle": brief.get("subtitle") or None,
        "authors": [str(a) for a in (brief.get("authors") or []) if a],
        "year": str(brief["publicationDate"]) if brief.get("publicationDate") else None,
        "url": record,
        "coverUrl": cover_of(bib),
        "medium": medium,
        "format": fmt,
        "formatLabel": label,
        "provider": provider_of(bib) if medium == "digital" else None,
        "availability": availability,
        "holdable": holdable,
        "borrowUrl": borrow_url,
    }


def items_from_search(library: Library, response: dict, medium: str) -> List[dict]:
    """Items in the search's own order, filtered to the requested medium.

    The gateway's ``f_STATUS`` filter narrows the search, but it is not exact — the online
    filter returned KCLS's hoopla music, which is marked ``PHYSICAL`` — so every item is
    classified here and the protocol's promise ("every item matches the filter") is kept by
    this function, not by trusting the facet.
    """
    bibs = (response.get("entities") or {}).get("bibs") or {}
    results = (response.get("catalogSearch") or {}).get("results") or []
    ordered = _ordered_ids(results) or list(bibs.keys())

    items = []
    for metadata_id in ordered:
        bib = bibs.get(metadata_id)
        if not isinstance(bib, dict):
            continue
        item = item_from_bib(library, bib)
        if item is not None and (medium == "any" or item["medium"] == medium):
            items.append(item)
    return items


def _ordered_ids(results: Iterable) -> List[str]:
    ids = []
    for entry in results:
        if isinstance(entry, dict) and entry.get("representative"):
            ids.append(entry["representative"])
        elif isinstance(entry, str):
            ids.append(entry)
    return ids


def status_filter(medium: str) -> Optional[str]:
    return {"physical": PHYSICAL_FILTER, "digital": DIGITAL_FILTER}.get(medium)


def pickup_locations(response: dict) -> List[dict]:
    """Branches from a search's STATUS facet, minus the ones nobody collects from.

    BiblioCommons exposes no public branch list; the facet of a broad physical search is the
    closest thing, and it carries each branch's code. It is best effort — a hold placed
    without a pickup location goes to the account's own default branch, which is always
    right — so a branch missing here costs a choice, never a hold.
    """
    for field in (response.get("catalogSearch") or {}).get("fields") or []:
        if field.get("id") != "STATUS":
            continue
        locations = []
        for entry in field.get("fieldFilters") or []:
            code, name = str(entry.get("value") or ""), str(entry.get("label") or "").strip()
            if not code or code.startswith("_") or not name:
                continue
            if any(word in name.lower() for word in NOT_PICKUP):
                continue
            locations.append({"id": code, "name": name})
        return sorted(locations, key=lambda location: location["name"].lower())
    return []
