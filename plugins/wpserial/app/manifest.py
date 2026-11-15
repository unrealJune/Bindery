"""The manifest.

WordPress serials live on every domain there is, so a fixed `matches` list can only ever name
a few. The rest is `probe`: it claims WordPress.com blogs, URLs shaped like WordPress
permalinks (`?p=123`, `/2011/06/14/slug/`, `/chapter/slug/`), hosts listed in
`WPSERIAL_HOSTS`, and hosts this plugin has already bound a book from. For anything else,
choose this plugin explicitly in Bindery's Routing menu once; after a successful download the
host is remembered and routes here by itself.

Priority is low on purpose. A site with a dedicated plugin — FanFicFare's adapters, or
Hedgerow for hungryshedgerow.net with its Discord merge — should win the tiebreak; this is
the generic fallback.
"""

from __future__ import annotations

VERSION = "0.1.0"

PRIORITY = 50

MATCHES = [
    r"^https?://(www\.)?sunspot\.gay([/?#]|$)",
    r"^https?://(www\.)?hungryshedgerow\.net([/?#]|$)",
    r"^https?://[a-z0-9-]+\.wordpress\.com([/?#]|$)",
]

CONFIG = [
    {
        "key": "post_passwords",
        "label": "Post passwords",
        "type": "secret",
        "help": "For password-protected chapters (WordPress's 'Protected:' posts). Either one "
                "password for every site, or host=password pairs separated by semicolons.",
        "placeholder": "hungryshedgerow.net=hunter2; example.com=swordfish",
    },
    {
        "key": "request_delay_ms",
        "label": "Delay between requests (ms)",
        "type": "int",
        "default": "750",
        "min": 0,
        "max": 10000,
        "help": "Politeness gap between page loads. A first download of a long serial reads "
                "every chapter once; later updates reuse the plugin's cache.",
    },
    {
        "key": "embed_images",
        "label": "Include images",
        "type": "bool",
        "default": "true",
        "help": "Download images in chapters (and the cover) into the book.",
    },
    {
        "key": "max_image_kb",
        "label": "Largest image to include (KB)",
        "type": "int",
        "default": "0",
        "min": 0,
        "max": 10240,
        "help": "Leave out images larger than this, to keep books small. 0 means no limit. "
                "Smaller WordPress-generated sizes are already preferred when the site lists them.",
    },
    {
        "key": "user_agent",
        "label": "User agent",
        "type": "string",
        "help": "Override the identity presented to sites. Leave blank unless a site refuses "
                "requests.",
    },
]

ACTIONS = [
    {
        "name": "inspect",
        "label": "Inspect a serial",
        "description": "Show how a URL would be read — where the table of contents is and which "
                       "chapters it lists — without downloading the chapters.",
        "input": [
            {"key": "url", "label": "Serial URL", "type": "url", "required": True,
             "placeholder": "https://parahumans.wordpress.com/table-of-contents/"},
        ],
        "output": {"kind": "text"},
    },
    {
        "name": "clear_cache",
        "label": "Clear chapter cache",
        "description": "Forget cached chapter pages so the next download re-reads them — for one "
                       "site, or for every site when no URL is given.",
        "input": [
            {"key": "url", "label": "Any URL on the site (optional)", "type": "url", "required": False},
        ],
        "output": {"kind": "message"},
    },
]


def build_manifest() -> dict:
    return {
        "protocolVersion": 1,
        "name": "wpserial",
        "displayName": "WordPress serials",
        "version": VERSION,
        "homepage": "https://wordpress.org/",
        "description": "Binds web serials published on WordPress sites — table of contents, "
                       "Next Chapter links, protected posts — into EPUBs.",
        "priority": PRIORITY,
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
        "ui": {"mode": "declarative"},
    }
