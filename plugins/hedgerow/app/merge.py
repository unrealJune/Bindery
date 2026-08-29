"""Reconciling two sources into one ordered book.

The rules, all decided and not up for renegotiation here:

- **Ordering.** Chapters carry an `arc.part` number read from their title; those sort
  numerically. A chapter the parser cannot read falls back to its Royal Road publish date,
  then to the Discord message timestamp.
- **Manual override.** A user drag-and-drop rank (`manual_order`) beats everything; an
  explicit pinned number beats the parsed one. Both are set from the plugin's own UI.
- **Precedence.** First source to supply a chapter owns it — enforced in `store.py`, not
  here — so this module never has to choose between two bodies for the same chapter.

Parsing lives here so both the ingest path (which records `arc`/`part`) and the ordering
path (which consumes them) agree on exactly one definition of a chapter number.
"""

from __future__ import annotations

import re
from typing import List, Optional, Tuple

from .store import Chapter

# Bumped whenever a parser change would give an already-ingested chapter a different
# `chapter_key`. `Store.reindex_chapters` reads it and rewrites the stored keys once, which
# is what stops a parser improvement from re-ingesting the whole back catalogue as
# duplicates the next time either source supplies a chapter it already has.
KEY_VERSION = "2"

# Serials label their two levels with real words, and the words are not interchangeable:
# an arc is the major number and a chapter is the minor one, whichever order they appear in
# the title. 'Maidens of the Fall, Arc 6, Zodiacal Light, Chapter 3' is chapter 6.3 — which
# taking the last number in the title, as this once did, gets exactly backwards.
_MAJOR_WORDS = ("arc", "book", "volume", "vol", "season")
_MINOR_WORDS = ("chapter", "chap", "ch", "episode", "ep", "part", "pt", "section")

_MAJOR_SET = set(_MAJOR_WORDS)

# A label, any run of separators a title might use, then the number it labels. `\b` on both
# ends is what keeps 'March' from reading as 'ch' and 'Arc in the dark' from reading as a
# roman numeral.
_LABELLED = re.compile(
    r"\b(?P<label>" + "|".join(_MAJOR_WORDS + _MINOR_WORDS) + r")\b"
    r"[\s.:#\-–—]*"
    r"(?P<value>\d+(?:\.\d+)?|[ivxlcdm]+)\b",
    re.I,
)

# The unlabelled fallback: the LAST number in the title, which is where a serial's marker
# sits after any arc name. A decimal is arc.part; a bare integer is a whole chapter.
_BARE = re.compile(r"(\d+)(?:\.(\d+))?(?!.*\d)")

_ROMAN = re.compile(r"^M{0,3}(CM|CD|D?C{0,3})(XC|XL|L?X{0,3})(IX|IV|V?I{0,3})$", re.I)
_ROMAN_DIGITS = {"i": 1, "v": 5, "x": 10, "l": 50, "c": 100, "d": 500, "m": 1000}


def _roman_value(text: str) -> Optional[int]:
    """A roman numeral's value, or None for anything that only looks like one.

    Validated strictly rather than summed loosely: 'Book Ill' must not become book 151.
    """
    if not text or not _ROMAN.match(text):
        return None
    lowered = text.lower()
    total, previous = 0, 0
    for char in reversed(lowered):
        value = _ROMAN_DIGITS[char]
        total += -value if value < previous else value
        previous = max(previous, value)
    return total or None


def _parse_value(raw: str) -> Optional[Tuple[int, Optional[int]]]:
    """(whole, fraction) for '6', '6.3', or 'VI'. None if it is not a number at all."""
    if "." in raw:
        whole, _, fraction = raw.partition(".")
        if whole.isdigit() and fraction.isdigit():
            return (int(whole), int(fraction))
        return None
    if raw.isdigit():
        return (int(raw), None)
    value = _roman_value(raw)
    return None if value is None else (value, None)


def parse_number(title: str) -> Tuple[Optional[int], Optional[int]]:
    """(arc, part) read from a chapter title, or (None, None) if there is no number.

    Labelled numbers win over unlabelled ones, and a major label ('Arc 6') always supplies
    the arc no matter where it sits relative to the minor one ('Chapter 3'). A title with
    two minor labels and no major reads the first as the arc, so 'Part 2, Chapter 5' is
    2.5. Failing all of that, the last number in the title is used as it always was, which
    is what keeps 'Zodiacal Light - 6.1' and a bare 'Chapter 7' reading correctly.
    """
    text = title or ""
    majors: List[Tuple[int, Optional[int]]] = []
    minors: List[Tuple[int, Optional[int]]] = []

    for match in _LABELLED.finditer(text):
        parsed = _parse_value(match.group("value"))
        if parsed is None:
            continue
        target = majors if match.group("label").lower() in _MAJOR_SET else minors
        target.append(parsed)

    if majors:
        arc, fraction = majors[-1]
        if minors:
            return (arc, minors[-1][0])
        return (arc, fraction or 0)

    if len(minors) >= 2:
        return (minors[0][0], minors[-1][0])

    if minors:
        whole, fraction = minors[0]
        return (whole, fraction or 0)

    match = _BARE.search(text)
    if not match:
        return (None, None)
    return (int(match.group(1)), int(match.group(2)) if match.group(2) is not None else 0)


def chapter_key(title: str) -> str:
    """The identity two sources must agree on for first-seen dedup to fire.

    A parsed number is the strongest signal — the same chapter posted to Royal Road and
    forwarded as an EPUB will share ``6.3`` even when one spells it 'Arc 6 ... Chapter 3'
    and the other writes '6.3'. Without a number, fall back to a normalized title.
    """
    arc, part = parse_number(title)
    if arc is not None:
        return f"n:{arc}.{part}"
    return "t:" + re.sub(r"\s+", " ", (title or "").strip().lower())


def _sort_key(chapter: Chapter) -> tuple:
    """A total order across chapters that may be numbered, dated, or neither.

    Each tier is (rank, ...values). Lower tiers sort first, so manual order wins, then pins,
    then parsed numbers, then publish date, then the Discord timestamp, then a stable id.
    """
    if chapter.manual_order is not None:
        return (0, chapter.manual_order, chapter.id)
    if chapter.pinned_number is not None:
        return (1, chapter.pinned_number, chapter.id)
    if chapter.arc is not None:
        return (2, chapter.arc, chapter.part or 0, chapter.id)
    if chapter.published:
        return (3, chapter.published, chapter.id)
    if chapter.discord_ts is not None:
        return (4, chapter.discord_ts, chapter.id)
    return (5, chapter.first_seen, chapter.id)


def order_chapters(chapters: List[Chapter]) -> List[Chapter]:
    return sorted(chapters, key=_sort_key)


def effective_number(chapter: Chapter) -> Optional[str]:
    """The number Hedgerow will show for a chapter, pin or parse, for display only."""
    if chapter.pinned_number is not None:
        number = chapter.pinned_number
        return str(int(number)) if float(number).is_integer() else str(number)
    if chapter.arc is not None:
        return f"{chapter.arc}.{chapter.part}" if chapter.part else str(chapter.arc)
    return None
