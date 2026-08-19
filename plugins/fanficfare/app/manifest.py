"""The manifest, most of which FanFicFare tells us about itself.

The supported-site list is not hardcoded: it is read out of the installed FanFicFare at
startup. Bump the image's FanFicFare version and the URLs Bindery routes here change with
it, with no edit to this file and no Bindery rebuild. That is the whole point of the
sidecar architecture, expressed in about twenty lines.
"""

from __future__ import annotations

import os
import re
from functools import lru_cache

VERSION = "0.1.0"

# FanFicFare ships adapters for four fake sites that fabricate stories offline. They are
# how this plugin is tested without touching a real fanfic site, and they are excluded
# from the manifest unless explicitly enabled.
TEST_SITES = ("test1.com", "test2.com", "test3.com", "test4.com")
ALLOW_TEST_SITES = os.environ.get("BINDERY_FFF_ALLOW_TEST_SITES", "").lower() in ("1", "true", "yes")


@lru_cache(maxsize=1)
def fanficfare_version() -> str:
    """The installed FanFicFare's version.

    Read from package metadata rather than by importing `fanficfare.cli`, which is where
    the constant actually lives but which drags in the whole command-line surface and
    mutates the environment on import.
    """
    from importlib.metadata import PackageNotFoundError, version

    try:
        return version("FanFicFare")
    except PackageNotFoundError:  # pragma: no cover - only when FanFicFare is missing
        return "unknown"


@lru_cache(maxsize=1)
def site_domains() -> tuple:
    """Every domain the installed FanFicFare has an adapter for."""
    from fanficfare import adapters  # type: ignore

    domains = []
    for domain in adapters.getSiteSections():
        if domain in TEST_SITES and not ALLOW_TEST_SITES:
            continue
        domains.append(domain)
    return tuple(sorted(set(domains)))


@lru_cache(maxsize=1)
def site_examples() -> tuple:
    """(domain, [example urls]) for the sites this plugin claims."""
    from fanficfare import adapters  # type: ignore

    return tuple(
        (domain, list(examples))
        for domain, examples in sorted(adapters.getSiteExamples())
        if domain in site_domains()
    )


def url_patterns() -> list:
    """One anchored regex per supported domain.

    `www.` is made optional rather than trusted, and the domain must be followed by `/`
    or `?` so that `archiveofourown.org.evil.test` does not match.
    """
    patterns = []
    for domain in site_domains():
        bare = domain[4:] if domain.startswith("www.") else domain
        patterns.append(r"^https?://(www\.)?" + re.escape(bare) + r"[/?]")
    return patterns


CONFIG = [
    {
        "key": "ao3_username",
        "label": "AO3 username",
        "type": "string",
        "help": "Only needed for works restricted to logged-in users.",
        "placeholder": "username",
    },
    {
        "key": "ao3_password",
        "label": "AO3 password",
        "type": "secret",
        "help": "Stored encrypted by Bindery and sent only to this plugin.",
    },
    {
        "key": "is_adult",
        "label": "Confirm adult status",
        "type": "bool",
        "default": "false",
        "help": "Several sites gate mature works behind an age confirmation.",
    },
    {
        "key": "include_images",
        "label": "Include images",
        "type": "bool",
        "default": "false",
        "help": "Embeds illustrations in the EPUB. Larger files, slower downloads.",
    },
    {
        "key": "user_agent",
        "label": "User agent",
        "type": "string",
        "help": "Override the browser identity FanFicFare presents. Leave blank unless a site is refusing you.",
    },
    {
        "key": "personal_ini",
        "label": "personal.ini overrides",
        "type": "text",
        "help": "FanFicFare configuration, applied on top of the fields above. Validate it under the plugin's own UI.",
        "placeholder": "[archiveofourown.org]\nusername:someone\n",
    },
]

ACTIONS = [
    {
        "name": "list_urls",
        "label": "List stories on a page",
        "description": (
            "Give an author page, series, collection, or bookmark list and get back every "
            "story URL on it, ready to queue."
        ),
        "input": [
            {
                "key": "url",
                "label": "Page URL",
                "type": "url",
                "required": True,
                "placeholder": "https://archiveofourown.org/users/someone/works",
            }
        ],
        "output": {"kind": "list", "itemAction": "download"},
    }
]

UI = {
    "mode": "fragment",
    "nav": [
        {"label": "FanFicFare", "path": "/", "icon": "book"},
        {"label": "Supported sites", "path": "/sites", "icon": "globe"},
        {"label": "personal.ini", "path": "/ini", "icon": "settings", "section": "settings"},
    ],
}


def build_manifest() -> dict:
    return {
        "protocolVersion": 1,
        "name": "fanficfare",
        "displayName": "FanFicFare",
        "version": VERSION,
        "homepage": "https://github.com/JimmXinu/FanFicFare",
        "description": (
            f"Downloads fanfiction from {len(site_domains())} sites using FanFicFare "
            f"{fanficfare_version()}."
        ),
        "priority": 100,
        "matches": url_patterns(),
        "formats": ["epub", "html", "txt"],
        "capabilities": {
            "probe": True,
            "update": True,
            "metadata": True,
            "cover": True,
            "cancel": True,
        },
        "config": CONFIG,
        "actions": ACTIONS,
        "ui": UI,
    }
