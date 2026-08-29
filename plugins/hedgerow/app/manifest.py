"""The manifest.

Unlike the FanFicFare plugin, the match list is fixed rather than read out of a library:
Hedgerow serves exactly one serial, from Royal Road and its hungryshedgerow.net mirror.
"""

from __future__ import annotations

VERSION = "0.2.0"

# Royal Road fiction and chapter pages, and the author's own mirror. Anchored, www optional,
# and the domain must be followed by a path/query boundary so 'royalroad.com.evil' cannot match.
MATCHES = [
    r"^https?://(www\.)?royalroad\.com/fiction/\d+[/?]?",
    r"^https?://(www\.)?hungryshedgerow\.net[/?]",
]

CONFIG = [
    {
        "key": "discord_token",
        "label": "Discord bot token",
        "type": "secret",
        "help": "Enables ingesting advance-copy EPUBs. The bot needs the MESSAGE CONTENT "
                "privileged intent. Leave blank to run Royal-Road-only.",
    },
    {
        "key": "discord_channels",
        "label": "Discord channel IDs",
        "type": "string",
        "help": "Comma-separated channel IDs to watch. A Royal Road URL posted in a channel "
                "binds it to that work; EPUBs posted afterwards attach to it.",
        "placeholder": "112233445566778899, 998877665544332211",
    },
    {
        "key": "hedgerow_password",
        "label": "hungryshedgerow.net password",
        "type": "secret",
        "help": "Optional. Only needed if the mirror gates chapters behind a post password.",
    },
    {
        "key": "is_adult",
        "label": "Confirm adult status",
        "type": "bool",
        "default": "false",
        "help": "Royal Road gates some mature works behind an age confirmation.",
    },
    {
        "key": "user_agent",
        "label": "User agent",
        "type": "string",
        "help": "Override the browser identity presented to Royal Road. Leave blank unless a "
                "fetch is being refused.",
    },
]

ACTIONS = [
    {
        "name": "pending",
        "label": "Pending works",
        "description": "List works that have ingested chapters but have not been downloaded "
                       "into the library yet.",
        "input": [],
        "output": {"kind": "list", "itemAction": "download"},
    },
    {
        "name": "rescan",
        "label": "Rescan Royal Road",
        "description": "Re-fetch a work from Royal Road and reconcile it with what Discord has "
                       "supplied, without waiting for the host's update schedule.",
        "input": [
            {
                "key": "url",
                "label": "Royal Road URL",
                "type": "url",
                "required": True,
                "placeholder": "https://www.royalroad.com/fiction/12345",
            }
        ],
        "output": {"kind": "message"},
    },
    {
        "name": "forget",
        "label": "Forget a work",
        "description": "Stop tracking a work: its ingested chapters and any channel bound "
                       "to it are deleted. A book already filed in the library is untouched.",
        "input": [
            {
                "key": "url",
                "label": "Royal Road or hungryshedgerow URL",
                "type": "url",
                "required": True,
                "placeholder": "https://www.royalroad.com/fiction/12345",
            }
        ],
        "output": {"kind": "message"},
    },
]

UI = {
    "mode": "sandboxed",
    "entry": "/",
    "nav": [
        {"label": "Chapters", "path": "/", "icon": "book"},
        {"label": "Sources", "path": "/sources", "icon": "settings", "section": "settings"},
    ],
}


def build_manifest() -> dict:
    return {
        "protocolVersion": 1,
        "name": "hedgerow",
        "displayName": "Hedgerow",
        "version": VERSION,
        "homepage": "https://www.royalroad.com/",
        "description": "Reconciles a web serial from Royal Road and Discord advance-copy "
                       "EPUBs into a single, correctly ordered book.",
        # Higher than FanFicFare's 100: FanFicFare also claims royalroad.com, but for this
        # serial Hedgerow's two-source merge must win the routing tiebreak.
        "priority": 200,
        "matches": MATCHES,
        "formats": ["epub"],
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
