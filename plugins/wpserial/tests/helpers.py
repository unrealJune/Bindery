"""Fixtures and a fake fetcher, so the crawl can be tested with no network at all.

The fixture pages are shaped like the real sites — the same themes, classes, ids, and link
patterns, captured November 2026 — with the story text replaced by a line or two.
"""

import os
from typing import Dict, Optional, Tuple

from app.fetch import FetchError, Response

FIXTURES = os.path.join(os.path.dirname(__file__), "fixtures")


def fixture(name: str) -> str:
    with open(os.path.join(FIXTURES, name), encoding="utf-8") as handle:
        return handle.read()


def post(post_id: int, title: str, body: str, *, categories=("serial",), nxt: Optional[str] = None,
         prev: Optional[str] = None, theme_next: Optional[str] = None, protected: bool = False,
         date: str = "2026-01-01T00:00:00+00:00") -> str:
    """A minimal twentyeleven single post."""
    cats = " ".join(f"category-{c}" for c in categories)
    links = ""
    if prev:
        links += f'<a href="{prev}">Previous Chapter</a> '
    if nxt:
        links += f'<a href="{nxt}">Next Chapter</a>'
    content = ('<form action="https://serial.example.org/wp-login.php?action=postpass" class="post-password-form">'
               '<input name="post_password"/></form>' if protected else f"<p>{links}</p>{body}<p>{links}</p>")
    theme = f'<nav id="nav-single"><span class="nav-next"><a href="{theme_next}" rel="next">Next</a></span></nav>' \
        if theme_next else ""
    locked = " post-password-required" if protected else ""
    return f"""<!DOCTYPE html><html lang="en"><head><title>{title} | Serial</title>
<meta property="og:site_name" content="Serial"/><link rel="https://api.w.org/" href="/wp-json/"/></head>
<body class="single single-post postid-{post_id}"><div id="content">{theme}
<article id="post-{post_id}" class="post-{post_id} post hentry {cats}{locked}">
<header class="entry-header"><h1 class="entry-title">{'Protected: ' if protected else ''}{title}</h1>
<time class="entry-date" datetime="{date}">x</time>
<span class="author vcard"><a href="/author/a/" rel="author">Author Person</a></span></header>
<div class="entry-content">{content}</div></article></div></body></html>"""


class FakeFetcher:
    """Serves pages from a dict: url -> html, or url -> (final_url, html) for a redirect."""

    def __init__(self, pages: Dict[str, object], password: Optional[str] = None,
                 unlocked: Optional[Dict[str, str]] = None):
        self.pages = pages
        self.password = password
        self.unlocked_pages = unlocked or {}
        self.unlocked = False
        self.requests = 0
        self.fetched = []

    def get(self, url: str, **_kwargs) -> Response:
        self.requests += 1
        self.fetched.append(url)
        if self.unlocked and url in self.unlocked_pages:
            return Response(url=url, status=200, body=self.unlocked_pages[url].encode(),
                            headers={"content-type": "text/html; charset=UTF-8"})
        entry = self.pages.get(url)
        if entry is None:
            raise FetchError("not_found", f"{url} does not exist (404)")
        final, html = entry if isinstance(entry, tuple) else (url, entry)
        if isinstance(html, bytes):
            return Response(url=final, status=200, body=html, headers={"content-type": "image/png"})
        return Response(url=final, status=200, body=html.encode("utf-8"),
                        headers={"content-type": "text/html; charset=UTF-8"})

    def password_for(self, url: str) -> Optional[str]:
        return self.password

    def unlock(self, action_url: str, page_url: str) -> bool:
        self.unlocked = self.password == "right"
        return self.unlocked


def pages(**items) -> Dict[str, Tuple[str, str]]:
    return dict(items)
