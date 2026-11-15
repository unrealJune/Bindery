"""Whether a URL is this plugin's, answered without touching the network (protocol 3.3).

WordPress serials live on every domain there is, so a pattern list cannot cover them. The
probe claims what it can recognise offline, and declines the rest so Bindery can explain
"no plugin claimed this" instead of routing everything here.
"""

from __future__ import annotations

import os
import re
from typing import Iterable

from .fetch import bare_host, split
from .manifest import MATCHES

EXTRA_HOSTS = {bare_host(h.strip()) for h in os.environ.get("WPSERIAL_HOSTS", "").split(",") if h.strip()}

# Never claimed: reserved names (RFC 2606/6761) and hosts that cannot be a public serial.
_RESERVED = re.compile(r"(?:^|\.)(?:invalid|test|example|localhost|local|internal)$|^(?:\d+\.){3}\d+$", re.I)
_WP_SHAPED = [
    re.compile(r"[?&](?:p|page_id)=\d+(?:&|$)"),
    re.compile(r"/\d{4}/\d{2}/(?:\d{2}/)?[^/]+/?$"),
    re.compile(r"/(?:chapter|chapters|serial|story|table-of-contents|toc)(?:/|-|$)", re.I),
    re.compile(r"/category/[^/]+/"),
]


def probe_url(url: str, known_hosts: Iterable[str] = ()) -> dict:
    parts = split(url)
    host = bare_host(parts.hostname or "") if parts is not None else ""
    if parts is None or parts.scheme not in ("http", "https") or not host or _RESERVED.search(host):
        return {"supported": False, "confidence": 0.0, "reason": "not a public http(s) URL"}
    # A probe-capable plugin is asked even about URLs its own patterns matched, and a "no"
    # removes it from the running, so the probe must agree with the manifest.
    if any(re.match(pattern, url.strip(), re.I) for pattern in MATCHES):
        return {"supported": True, "confidence": 1.0, "reason": "listed WordPress serial site"}
    if host.endswith(".wordpress.com"):
        return {"supported": True, "confidence": 0.9, "reason": "WordPress.com site"}
    if host in EXTRA_HOSTS or host in known_hosts:
        return {"supported": True, "confidence": 0.9, "reason": f"{host} is a known WordPress serial host"}
    target = parts.path + ("?" + parts.query if parts.query else "")
    if any(pattern.search(target) for pattern in _WP_SHAPED):
        return {"supported": True, "confidence": 0.4, "reason": "URL is shaped like a WordPress permalink"}
    return {"supported": False, "confidence": 0.0,
            "reason": "unknown host; choose this plugin explicitly to try it"}
