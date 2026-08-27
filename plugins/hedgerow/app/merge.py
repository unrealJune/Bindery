"""Reconciling two sources into one ordered book.

The rules, all decided and not up for renegotiation here:

- **Ordering.** Chapters carry an `arc.part` number parsed from their title (``6.1``) or a
  bare chapter number (``7``); those sort numerically. A chapter the parser cannot read
  falls back to its Royal Road publish date, then to the Discord message timestamp.
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

# 'Zodiacal Light – 6.1', 'Chapter 7', '7' — take the LAST number in the title, which is
# where a serial's chapter marker sits after any arc name. A decimal is arc.part; a bare
# integer is a whole chapter, treated as arc N, part 0 so it sorts before its own sub-parts.
_NUMBER = re.compile(r"(\d+)(?:\.(\d+))?(?!.*\d)")


def parse_number(title: str) -> Tuple[Optional[int], Optional[int]]:
    """(arc, part) parsed from a chapter title, or (None, None) if there is no number."""
    match = _NUMBER.search(title or "")
    if not match:
        return (None, None)
    arc = int(match.group(1))
    part = int(match.group(2)) if match.group(2) is not None else 0
    return (arc, part)


def chapter_key(title: str) -> str:
    """The identity two sources must agree on for first-seen dedup to fire.

    A parsed number is the strongest signal — the same chapter posted to Royal Road and
    forwarded as an EPUB will share ``6.1`` even if the wording around it differs. Without a
    number, fall back to a normalized title.
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
