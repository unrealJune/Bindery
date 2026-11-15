"""From a URL someone pasted to an ordered list of clean chapters.

The plan, in the order a person would do it by hand:

1. **Find the table of contents.** The pasted page may *be* the TOC (Worm's), may link to it
   ("Click here for the chapters!" on Hedgerow's landing page, "Table of Contents" in
   Sunspot's menu), or may be a chapter whose page links to it.
2. **Read every chapter the TOC lists**, in TOC order. Links that turn out not to be story —
   a "side stories" index, an About page full of links — are dropped after loading, not
   guessed at beforehand.
3. **Follow "Next Chapter" past the end of the TOC.** Authors update the TOC late; the newest
   chapters are often reachable only from the previous one. A link the author placed in the
   post is followed freely; the theme's chronological next-post link is followed only while
   it stays in the same category, because the next *post* on a blog may be an announcement or
   another story entirely. A locked chapter ends the walk.
4. With no TOC at all, start from a "Start Reading" link (or walk "Previous" back from a
   pasted chapter) and do step 3 from there.

Chapters are identified by WordPress post id (`article#post-N`), not URL: the same post is
routinely reachable as `?p=45`, a pretty permalink, an old slug that redirects, and — on
WordPress.com — a per-chapter category archive.
"""

from __future__ import annotations

import hashlib
import logging
import re
from dataclasses import asdict, dataclass, field
from typing import Callable, Dict, List, Optional, Set, Tuple

from . import clean, site
from .cache import Cache
from .fetch import MAX_IMAGE, Cancelled, Fetcher, FetchError, bare_host

log = logging.getLogger("bindery.wpserial")

MAX_CHAPTERS = 5000
MAX_WALK_BACK = 2000
REFRESH_TAIL = 3  # re-read the newest few chapters every time: authors fix typos in them
IMAGE_TYPES = {
    "image/jpeg": "jpg", "image/jpg": "jpg", "image/png": "png", "image/gif": "gif", "image/webp": "webp",
}


class CrawlError(Exception):
    def __init__(self, code: str, message: str, retryable: Optional[bool] = None):
        super().__init__(message)
        self.code = code
        self.message = message
        self.retryable = code in ("network", "rate_limited", "internal") if retryable is None else retryable


@dataclass
class Entry:
    """A chapter as the book needs it — and as the cache stores it."""

    key: str
    url: str
    title: str
    body: str = ""
    images: List[str] = field(default_factory=list)
    published: Optional[str] = None
    author: Optional[str] = None
    categories: List[str] = field(default_factory=list)
    next: Optional[Tuple[str, str]] = None
    prev: Optional[Tuple[str, str]] = None
    locked: Optional[str] = None

    def to_json(self) -> dict:
        return asdict(self)

    @classmethod
    def from_json(cls, data: dict) -> "Entry":
        fields = {k: data.get(k) for k in cls.__dataclass_fields__}
        for key in ("next", "prev"):
            if fields[key]:
                fields[key] = tuple(fields[key])
        fields["images"] = list(fields.get("images") or [])
        fields["categories"] = list(fields.get("categories") or [])
        return cls(**fields)


@dataclass
class Plan:
    start: site.Page
    toc: Optional[site.Page]
    entries: List[str]
    mode: str  # "toc" | "crawl" | "crawl-back"
    first: Optional[str] = None

    def describe(self) -> str:
        if self.mode == "toc":
            return f"table of contents at {self.toc.url if self.toc else '?'} lists {len(self.entries)} link(s)"
        if self.mode == "crawl":
            return f"no table of contents; following Next links from {self.first}"
        return f"no table of contents; walking back from {self.first}, then forward"


@dataclass
class Book:
    title: str
    author: Optional[str]
    description: Optional[str]
    language: str
    site_name: str
    source_url: str
    source_id: str
    chapters: List[Entry]
    images: Dict[str, Tuple[bytes, str]]  # filename under images/ -> (bytes, media type)
    cover: Optional[Tuple[bytes, str]]
    plan: str
    skipped: int = 0

    @property
    def published(self) -> Optional[str]:
        dates = [c.published for c in self.chapters if c.published]
        return min(dates) if dates else None

    @property
    def updated(self) -> Optional[str]:
        dates = [c.published for c in self.chapters if c.published]
        return max(dates) if dates else None


def source_id(url: str) -> str:
    return "wpserial:" + site.normalize(url)


def source_url(url: str) -> str:
    return site.absolute(url, url) or url


class Crawler:
    def __init__(self, fetcher: Fetcher, cache: Cache, emit: Callable[[dict], None] = lambda e: None, *,
                 embed_images: bool = True, cancelled: Callable[[], bool] = lambda: False,
                 max_chapters: int = MAX_CHAPTERS, max_image_bytes: int = MAX_IMAGE):
        self.fetcher = fetcher
        self.cache = cache
        self.emit = emit
        self.embed_images = embed_images
        self.cancelled = cancelled
        self.max_chapters = max_chapters
        self.max_image_bytes = max_image_bytes if max_image_bytes > 0 else MAX_IMAGE
        self._unlock_failed: Set[str] = set()
        self.fetched_pages = 0

    # ------------------------------------------------------------ events

    def progress(self, percent: Optional[float], message: str) -> None:
        event = {"event": "progress", "message": message[:200]}
        if percent is not None:
            event["percent"] = round(max(0.0, min(100.0, percent)), 1)
        self.emit(event)

    def log(self, level: str, message: str) -> None:
        self.emit({"event": "log", "level": level, "message": message[:500]})

    def _check(self) -> None:
        if self.cancelled():
            raise Cancelled()

    # ------------------------------------------------------------ pages

    def page(self, url: str) -> site.Page:
        self._check()
        response = self.fetcher.get(url)
        self.fetched_pages += 1
        if response.content_type and "html" not in response.content_type:
            raise CrawlError("unsupported_url", f"{url} is not an HTML page ({response.content_type})")
        return site.analyze(response.url, response.text)

    # ------------------------------------------------------------ discovery

    def discover(self, url: str) -> Plan:
        try:
            start = self.page(url)
        except FetchError as exc:
            raise CrawlError(exc.code, exc.message, exc.retryable)
        if not start.wordpress:
            raise CrawlError("unsupported_url", f"{bare_host(start.url)} does not look like a WordPress site")

        if site.is_post(start):
            toc = self._follow_toc(start)
            if toc is not None:
                return toc
            return Plan(start, None, [], "crawl-back", first=start.url)

        links = site.toc_links(start)
        if links and site.is_toc_page(start, links):
            return Plan(start, start, [l.url for l in links], "toc")
        toc = self._follow_toc(start)
        if toc is not None:
            return toc
        first = site.find_start_link(start)
        if first:
            return Plan(start, None, [], "crawl", first=first)
        if links:
            return Plan(start, start, [l.url for l in links], "toc")
        raise CrawlError("parse", f"found no table of contents, first-chapter link, or chapter links on {start.url}")

    def _follow_toc(self, start: site.Page) -> Optional[Plan]:
        toc_url = site.find_toc_link(start)
        if not toc_url:
            return None
        try:
            toc = self.page(toc_url)
        except (FetchError, CrawlError) as exc:
            self.log("warn", f"table of contents link {toc_url} failed: {getattr(exc, 'message', exc)}")
            return None
        links = site.toc_links(toc)
        if not links:
            return None
        return Plan(start, toc, [l.url for l in links], "toc")

    # ------------------------------------------------------------ the crawl

    def run(self, url: str, previous_urls: Optional[List[str]] = None) -> Book:
        self.progress(1, "reading the start page")
        plan = self.discover(url)
        self.log("info", plan.describe())

        refresh: Set[str] = set((previous_urls or [])[-REFRESH_TAIL:])
        refresh |= set(plan.entries[-REFRESH_TAIL:])
        chapters: List[Entry] = []
        keys: Set[str] = set()
        visited: Set[str] = set()
        skipped = 0

        def accept(entry: Entry) -> bool:
            if entry.key in keys:
                return False
            keys.add(entry.key)
            chapters.append(entry)
            return True

        if plan.mode == "toc":
            total = len(plan.entries)
            for index, link in enumerate(plan.entries, start=1):
                self._check()
                visited.add(site.normalize(link))
                entries = self._read(link, fresh=link in refresh, from_toc=True)
                if not entries:
                    skipped += 1
                for entry in entries:
                    visited.add(site.normalize(entry.url))
                    if entry.locked:
                        skipped += 1
                        self.log("warn", f"skipping locked chapter ({entry.locked}): {entry.title or entry.url}")
                    elif accept(entry):
                        self.progress(5 + 75 * index / total, f"chapter {index}/{total}: {entry.title}")
        else:
            first_url = plan.first or plan.start.url
            entries = self._read(first_url, fresh=True, from_toc=False)
            readable = [e for e in entries if not e.locked]
            if not readable:
                raise CrawlError("parse", f"{first_url} has no readable chapter text")
            if plan.mode == "crawl-back":
                for entry in self._walk_back(readable[0], refresh):
                    accept(entry)
                    visited.add(site.normalize(entry.url))
            for entry in readable:
                visited.add(site.normalize(entry.url))
                accept(entry)
            self.progress(10, f"starting from {readable[0].title}")

        if not chapters:
            raise CrawlError("not_found", "no readable chapters were found"
                             + (f" ({skipped} link(s) were locked or not chapters)" if skipped else ""))

        added = self._walk_forward(chapters, keys, visited, refresh)
        if added:
            self.log("info", f"found {added} chapter(s) past the end of the table of contents"
                     if plan.mode == "toc" else f"followed Next links through {added} chapter(s)")

        title = site.serial_title(plan.start, plan.toc)
        for number, entry in enumerate(chapters, start=1):
            entry.title = _without_series(entry.title, title) or f"Chapter {number}"

        images = self._images(chapters)
        cover = self._cover(plan)
        start = plan.start
        return Book(
            title=title,
            author=site.first_author_name(start) or _first([c.author for c in chapters]),
            description=site.description(start) if not site.is_post(start) else
            (site.description(plan.toc) if plan.toc else None),
            language=(start.language or "en"),
            site_name=start.site_name,
            source_url=source_url(url),
            source_id=source_id(url),
            chapters=chapters,
            images=images,
            cover=cover,
            plan=plan.describe(),
            skipped=skipped,
        )

    def _walk_forward(self, chapters: List[Entry], keys: Set[str], visited: Set[str], refresh: Set[str]) -> int:
        added = 0
        current = chapters[-1]
        while len(chapters) < self.max_chapters:
            self._check()
            if not current.next:
                break
            url, how = current.next
            if site.normalize(url) in visited:
                break
            visited.add(site.normalize(url))
            entries = self._read(url, fresh=url in refresh, from_toc=False)
            if not entries:
                self.log("info", f"stopped at {url}: not a chapter")
                break
            entry = entries[0]
            if how == "theme" and not _related(current, entry):
                self.log("info", f"stopped at '{entry.title}': the next post is in a different category")
                break
            if entry.locked:
                self.log("warn", f"stopped at locked chapter ({entry.locked}): {entry.title}"
                         + (" — set a post password in the plugin's config to read it"
                            if entry.locked == "password" else ""))
                break
            if entry.key in keys:
                break
            keys.add(entry.key)
            chapters.append(entry)
            added += 1
            self.progress(80, f"chapter {len(chapters)}: {entry.title}")
            current = entry
        else:
            self.log("warn", f"stopped at the {self.max_chapters}-chapter limit")
        return added

    def _walk_back(self, start: Entry, refresh: Set[str]) -> List[Entry]:
        back: List[Entry] = []
        seen = {start.key}
        current = start
        while current.prev and len(back) < MAX_WALK_BACK:
            self._check()
            url, how = current.prev
            entries = self._read(url, fresh=url in refresh, from_toc=False)
            if not entries:
                break
            entry = entries[-1]
            if entry.key in seen or entry.locked or (how == "theme" and not _related(current, entry)):
                break
            seen.add(entry.key)
            back.append(entry)
            self.progress(None, f"walking back: {entry.title}")
            current = entry
        back.reverse()
        if back:
            self.log("info", f"walked back {len(back)} chapter(s) to '{back[0].title}'")
        return back

    # ------------------------------------------------------------ one chapter page

    def _read(self, url: str, *, fresh: bool, from_toc: bool) -> List[Entry]:
        if not fresh:
            cached = self.cache.get_chapters(url)
            # A cached page with no Next link was the newest chapter when it was cached: it is
            # exactly the page that may have gained one since.
            if cached is not None and (from_toc or all(c.get("next") for c in cached)):
                return [Entry.from_json(c) for c in cached]
        try:
            page = self.page(url)
        except FetchError as exc:
            if exc.code in ("not_found", "auth_required"):
                self.log("warn", f"skipping {url}: {exc.message}")
                return []
            raise CrawlError(exc.code, exc.message, exc.retryable)
        except CrawlError as exc:
            self.log("warn", f"skipping {url}: {exc.message}")
            return []

        if not site.accepts_as_chapter(page):
            if from_toc:
                self.log("debug", f"not a chapter, skipped: {url}")
            return []
        found = site.chapters(page)
        if any(c.locked == "password" for c in found):
            page, found = self._unlock(url, page, found)

        entries = [self._entry(page, chapter) for chapter in found]
        entries = [e for e in entries if e is not None]
        if entries and not any(e.locked for e in entries):
            self.cache.put_chapters(url, [e.to_json() for e in entries])
        return entries

    def _unlock(self, url: str, page: site.Page, found: List[site.Chapter]):
        host = bare_host(page.url)
        action = next((c.password_form for c in found if c.password_form), None)
        if host in self._unlock_failed or not action or not self.fetcher.password_for(page.url):
            return page, found
        if not self.fetcher.unlock(action, page.url):
            self._unlock_failed.add(host)
            self.log("warn", f"the post password for {host} was not accepted")
            return page, found
        page = self.page(url)
        found = site.chapters(page)
        if any(c.locked == "password" for c in found):
            self._unlock_failed.add(host)
            self.log("warn", f"the post password for {host} does not open {url}")
        return page, found

    def _entry(self, page: site.Page, chapter: site.Chapter) -> Optional[Entry]:
        nxt = site.next_link(page, chapter)
        prv = site.prev_link(page, chapter)
        entry = Entry(key=chapter.key, url=chapter.url, title=chapter.title, published=chapter.published,
                      author=chapter.author, categories=list(chapter.categories), next=nxt, prev=prv,
                      locked=chapter.locked)
        if chapter.locked or chapter.content is None:
            return entry
        clean.strip_furniture(chapter.content, page.url)
        entry.images = clean.image_urls(chapter.content, page.url) if self.embed_images else []
        entry.body = clean.serialize(chapter.content, page.url, self.embed_images)
        if not clean.text_length(entry.body) and not entry.images:
            self.log("debug", f"empty post skipped: {chapter.title or chapter.url}")
            return None
        return entry

    # ------------------------------------------------------------ images

    def _images(self, chapters: List[Entry]) -> Dict[str, Tuple[bytes, str]]:
        urls: List[str] = []
        seen: Set[str] = set()
        for chapter in chapters:
            for url in chapter.images:
                if url not in seen:
                    seen.add(url)
                    urls.append(url)
        result: Dict[str, Tuple[bytes, str]] = {}
        available: Dict[str, str] = {}
        for index, url in enumerate(urls, start=1):
            self._check()
            fetched = self._image(url)
            if fetched is None:
                continue
            data, media_type = fetched
            if len(data) > self.max_image_bytes:
                self.log("info", f"left out a {len(data) // 1024} KB image (over the size limit): {url}")
                continue
            token = clean.image_token(url)
            name = f"{token}.{IMAGE_TYPES[media_type]}"
            result[name] = (data, media_type)
            available[token] = name
            self.progress(85 + 10 * index / len(urls), f"image {index}/{len(urls)}")
        if len(available) < len(urls):
            self.log("info", f"{len(urls) - len(available)} image(s) could not be fetched and were left out")
        for chapter in chapters:
            if chapter.images or "images/" in chapter.body:
                chapter.body = clean.resolve_images(chapter.body, available)
        return result

    def _image(self, url: str) -> Optional[Tuple[bytes, str]]:
        cached = self.cache.get_image(url)
        if cached and cached[1] in IMAGE_TYPES:
            return cached
        try:
            response = self.fetcher.get(url, accept="image/avif,image/webp,image/png,image/*;q=0.8",
                                        max_bytes=MAX_IMAGE)
        except FetchError as exc:
            log.info("image %s: %s", url, exc.code)
            return None
        media_type = response.content_type
        if media_type not in IMAGE_TYPES:
            media_type = _sniff(response.body)
        if media_type not in IMAGE_TYPES:
            return None
        media_type = "image/jpeg" if media_type == "image/jpg" else media_type
        self.cache.put_image(url, response.body, media_type)
        return response.body, media_type

    def _cover(self, plan: Plan) -> Optional[Tuple[bytes, str]]:
        for page in (plan.start, plan.toc):
            if page is None:
                continue
            url = site.cover_url(page)
            if url and self.embed_images:
                fetched = self._image(url)
                if fetched:
                    return fetched
        return None


def _related(previous: Entry, entry: Entry) -> bool:
    """Same story? Shares a category, or either side has none to compare."""
    if not previous.categories or not entry.categories:
        return True
    return bool(set(previous.categories) & set(entry.categories))


def _without_series(chapter_title: str, series: str) -> str:
    """'Maidens of the Fall – Disarticulation – 1.1' reads as 'Disarticulation – 1.1' inside
    a book already called Maidens of the Fall."""
    if not chapter_title or not series:
        return chapter_title
    match = re.match(re.escape(series) + r"\s*[-–—:|,/]+\s*(\S.*)$", chapter_title, re.I)
    return match.group(1).strip() if match else chapter_title


def _first(values) -> Optional[str]:
    return next((v for v in values if v), None)


def _sniff(data: bytes) -> str:
    if data.startswith(b"\xff\xd8\xff"):
        return "image/jpeg"
    if data.startswith(b"\x89PNG\r\n\x1a\n"):
        return "image/png"
    if data[:6] in (b"GIF87a", b"GIF89a"):
        return "image/gif"
    if data[:4] == b"RIFF" and data[8:12] == b"WEBP":
        return "image/webp"
    return ""


def fingerprint(book: Book) -> str:
    """What the book would contain: chapter identities, titles, and text, plus its images."""
    digest = hashlib.sha256()
    for chapter in book.chapters:
        for part in (chapter.key, chapter.title, chapter.body):
            digest.update(part.encode("utf-8"))
            digest.update(b"\0")
    for name in sorted(book.images):
        digest.update(name.encode("utf-8"))
    digest.update(b"cover" if book.cover else b"")
    return digest.hexdigest()


def filename_for(title: str) -> str:
    cleaned = re.sub(r'[\x00-\x1f<>:"/\\|?*]+', " ", title or "").strip(" .")
    return (cleaned[:150] or "serial") + ".epub"

