"""The manifest: a circulation plugin (protocol §3.8) that downloads nothing."""

from __future__ import annotations

from . import libraries

VERSION = "0.1.0"


def build_manifest() -> dict:
    return {
        "protocolVersion": 1,
        "name": "bibliocommons",
        "displayName": "BiblioCommons libraries",
        "version": VERSION,
        "homepage": "https://www.bibliocommons.com/",
        "description": "Search your public library's catalog, place holds on physical books, and "
                       "track holds and loans — for any library whose catalog runs on "
                       "BiblioCommons, including Seattle Public Library and KCLS.",
        # Claims no URLs: there is nothing here to download. Circulation is the whole plugin.
        "matches": [],
        "formats": ["epub"],
        "capabilities": {
            "probe": False,
            "update": False,
            "metadata": False,
            "cover": False,
            "cancel": True,
            "notify": False,
            "circulation": True,
        },
        "config": [],
        "actions": [],
        "circulation": {
            "accountFields": [
                {
                    "key": "library",
                    "label": "Library",
                    "type": "select",
                    "required": True,
                    "default": next(iter(libraries.configured())),
                    "options": libraries.options(),
                },
                {
                    "key": "library_other",
                    "label": "Other library's BiblioCommons subdomain",
                    "type": "string",
                    "placeholder": "nypl",
                    "pattern": r"^[A-Za-z0-9][A-Za-z0-9-]{1,40}$",
                    "help": "Only for 'Another BiblioCommons library': the part before "
                            ".bibliocommons.com in your library's catalog address.",
                },
                {
                    "key": "card",
                    "label": "Library card number",
                    "type": "string",
                    "required": True,
                    "placeholder": "e.g. 1000012345678",
                },
                {
                    "key": "pin",
                    "label": "PIN or password",
                    "type": "secret",
                    "required": True,
                    "help": "The one you use to sign in to your library's catalog website.",
                },
            ],
            "mediums": ["physical", "digital"],
            # Physical holds at SPL and KCLS do not support suspension through the catalog
            # (policy.supportsSuspendHold is false), so only cancel is claimed.
            "holdOperations": ["cancel"],
        },
        "ui": {"mode": "declarative"},
    }
