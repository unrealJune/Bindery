"""Which BiblioCommons libraries this plugin knows, and how to reach any other one.

Every BiblioCommons library is the same software on a different subdomain:
``https://{slug}.bibliocommons.com`` for the catalog and
``https://gateway.bibliocommons.com/v2/libraries/{slug}`` for its JSON API. So supporting a
new library is a question of *naming* it, never of new code:

- the presets below appear in the account form's library list;
- an operator can add more with ``BIBLIOCOMMONS_LIBRARIES="nypl=New York Public Library,..."``;
- a person can always pick "Another BiblioCommons library" and type the subdomain.

The slug becomes part of a hostname, so it is validated against a strict pattern before it
is ever used. That pattern is the plugin's whole defence against being pointed at an
arbitrary host, and it is deliberately narrower than DNS allows.
"""

from __future__ import annotations

import os
import re
from dataclasses import dataclass
from typing import Dict, List, Optional

OTHER = "other"

SLUG_RE = re.compile(r"^[a-z0-9][a-z0-9-]{1,40}$")

# The two this plugin was built for. Order is the order of the account form's list.
PRESETS: Dict[str, str] = {
    "seattle": "The Seattle Public Library",
    "kcls": "King County Library System",
}


@dataclass(frozen=True)
class Library:
    slug: str
    name: str

    @property
    def catalog(self) -> str:
        return f"https://{self.slug}.bibliocommons.com"

    @property
    def gateway(self) -> str:
        return f"https://gateway.bibliocommons.com/v2/libraries/{self.slug}"

    def record_url(self, metadata_id: str) -> str:
        return f"{self.catalog}/v2/record/{metadata_id}"


class UnknownLibrary(ValueError):
    """The account names a library that cannot be a BiblioCommons subdomain."""


def configured() -> Dict[str, str]:
    """Presets plus whatever the operator added, operator entries winning on a clash."""
    libraries = dict(PRESETS)
    for entry in (os.environ.get("BIBLIOCOMMONS_LIBRARIES") or "").split(","):
        slug, _, name = entry.partition("=")
        slug = slug.strip().lower()
        if SLUG_RE.match(slug) and slug != OTHER:
            libraries[slug] = name.strip() or slug
    return libraries


def options() -> List[dict]:
    """The account form's library list (protocol §4 select options)."""
    choices = [{"value": slug, "label": name} for slug, name in configured().items()]
    choices.append({"value": OTHER, "label": "Another BiblioCommons library…"})
    return choices


def resolve(fields: dict) -> Library:
    """The library an account's fields name, or UnknownLibrary."""
    chosen = str(fields.get("library") or "").strip().lower()
    known = configured()

    if chosen == OTHER or (chosen and chosen not in known):
        slug = str(fields.get("library_other") or chosen).strip().lower()
        # Accept a pasted catalog address, but only one that genuinely ends in
        # .bibliocommons.com — anything else is left whole for the pattern to reject.
        slug = slug.removeprefix("https://").removeprefix("http://").rstrip("/")
        slug = slug.removesuffix(".bibliocommons.com")
    else:
        slug = chosen

    if not slug or slug == OTHER or not SLUG_RE.match(slug):
        raise UnknownLibrary(
            "choose a library, or enter the BiblioCommons subdomain — the 'nypl' in nypl.bibliocommons.com")

    return Library(slug=slug, name=known.get(slug, slug))


def name_from_sites(library: Library, sites: Optional[dict]) -> Library:
    """Upgrade a bare slug to the library's own name, from a gateway response's ``sites``."""
    if library.name != library.slug or not isinstance(sites, dict):
        return library
    for site in sites.values():
        if isinstance(site, dict) and site.get("subdomain") == library.slug and site.get("longName"):
            return Library(slug=library.slug, name=str(site["longName"]))
    return library
