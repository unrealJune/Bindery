"""Reading a WordPress page the way a reader would: what kind of page it is, where its story
text is, where the table of contents is, and where "Next Chapter" goes.

Everything here is a pure function of a URL and an HTML string, so it is unit tested against
markup shaped like the real sites (`tests/fixtures/`). Nothing in this module touches the
network; `crawl.py` decides what to fetch.

The heuristics are deliberately theme-agnostic. They lean on what WordPress itself emits —
`<article id="post-N">`, `.entry-content`, `.entry-title`, `post-password-required`,
`rel="next"` — and on the words authors put on links ("Next Chapter", "Table of Contents"),
not on any one site's markup.
"""

from __future__ import annotations

import re
import urllib.parse
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Iterable, List, Optional, Tuple

from . import dom
from .dom import Node
from .fetch import bare_host, split

# ---------------------------------------------------------------- vocabulary

CONTENT_CLASSES = (
    "entry-content", "post-content", "wp-block-post-content", "entry-body", "post-body",
    "article-content", "single-content", "post-entry", "entry", "content",
)

# Subtrees that are page furniture rather than story or index. Matched against whole class
# tokens and ids; the prefixes catch the families (`sd-sharing-enabled`, `widget_text`, ...).
JUNK_TAGS = frozenset({"script", "style", "noscript", "template", "form", "nav", "header", "footer",
                       "aside", "iframe", "object", "embed", "svg", "button", "select", "textarea", "input"})
JUNK_IDS = frozenset({"secondary", "comments", "respond", "colophon", "branding", "access", "nav-single",
                      "nav-above", "nav-below", "jp-post-flair", "jp-relatedposts", "sidebar",
                      "site-navigation", "masthead", "wpadminbar", "toc-legend"})
JUNK_CLASS_PREFIXES = ("sharedaddy", "sd-", "jp-relatedposts", "jp-post-flair", "widget", "comment",
                       "wpcnt", "wordads", "post-navigation", "nav-links", "nav-previous", "nav-next",
                       "wp-block-post-navigation", "navigation", "menu", "sidebar", "breadcrumb",
                       "related", "share", "social", "wp-block-buttons", "wp-block-button",
                       "boilerplate", "entry-meta", "entry-footer", "entry-utility", "post-meta",
                       "author-info", "author-bio", "site-header", "site-footer", "screen-reader",
                       "assistive-text", "patreon-flow", "subscribe", "newsletter", "likes",
                       "jetpack", "wp-block-jetpack", "code-block", "ad-", "ads-", "advert")

NEXT_TEXT = re.compile(
    r"^\s*(?:next|continue|forward)(?:\s+(?:chapter|part|page|post|episode|entry))?\s*"
    r"(?:[»›→>]+|-+>)?\s*$", re.I)
PREV_TEXT = re.compile(
    r"^\s*(?:[«‹←<]+|<-+)?\s*(?:prev|previous|last|back)(?:\s+(?:chapter|part|page|post|episode|entry))?\s*$",
    re.I)
TOC_TEXT = re.compile(
    r"\b(?:table\s+of\s+contents|contents|chapters|chapter\s+(?:list|index)|story\s+index|all\s+chapters|toc)\b",
    re.I)
TOC_TITLE = re.compile(r"^\s*(?:table\s+of\s+contents|contents|chapters|chapter\s+(?:list|index)|index|archive|toc)\s*$",
                       re.I)
TOC_SUFFIX = re.compile(
    r"\s*(?:[-–—:|/]\s*)?(?:table\s+of\s+contents|chapters?(?:\s+(?:list|index))?|contents|toc|index)\s*$", re.I)
START_TEXT = re.compile(
    r"\b(?:start\s+reading|begin\s+reading|read\s+from\s+the\s+(?:start|beginning)|first\s+chapter|"
    r"chapter\s+(?:1|one)\b|read\s+chapter\s+(?:1|one)\b|prologue|start\s+here|read\s+now)",
    re.I)
NAV_ONLY_TEXT = re.compile(
    r"^\s*(?:[«‹←<»›→>|•·\-–—\s]*(?:next|previous|prev|last|first|back|continue|index|home|"
    r"table\s+of\s+contents|contents|toc)(?:\s+(?:chapter|part|page|post|episode|entry))?[«‹←<»›→>|•·\-–—\s]*)+$",
    re.I)

# Paths that are never a chapter: WordPress plumbing, listings, and files.
NON_CHAPTER_PATH = re.compile(
    r"/(?:tag|author|feed|wp-admin|wp-content|wp-includes|wp-json|comments|search|xmlrpc\.php|wp-login\.php"
    r"|trackback|embed)(?:/|$)|/page/\d+/?$|\.(?:jpe?g|png|gif|webp|svg|pdf|zip|epub|mobi|mp3|mp4|css|js|xml|txt)$",
    re.I)
NON_CHAPTER_QUERY = re.compile(r"(?:^|&)(?:share|replytocom|s|action|patreon-login|patreon-final-redirect|"
                               r"like_comment|nb|subscribe)=", re.I)

PROTECTED_PREFIX = re.compile(r"^\s*(?:protected|private)\s*:\s*", re.I)
POST_ID = re.compile(r"^post-(\d+)$")
POSTID_CLASS = re.compile(r"^(?:postid|page-id)-(\d+)$")


# ---------------------------------------------------------------- data


@dataclass
class Link:
    url: str
    text: str
    rel: str = ""
    node: Optional[Node] = field(default=None, repr=False, compare=False)


@dataclass
class Chapter:
    """One article on a page, before its body is cleaned."""

    url: str
    key: str
    title: str
    content: Optional[Node] = field(repr=False, default=None)
    article: Optional[Node] = field(repr=False, default=None)
    published: Optional[str] = None
    author: Optional[str] = None
    categories: Tuple[str, ...] = ()
    locked: Optional[str] = None  # "password" | "patreon" | None
    password_form: Optional[str] = None  # post-password form action, when locked by password


@dataclass
class Page:
    url: str
    doc: Node
    body_classes: Tuple[str, ...]
    articles: List[Node]
    title: str
    site_name: str
    og: dict
    canonical: Optional[str]
    language: Optional[str]
    wordpress: bool

    @property
    def is_front_page(self) -> bool:
        parts = split(self.url)
        return "home" in self.body_classes or (parts is not None and parts.path in ("", "/"))

    @property
    def is_archive(self) -> bool:
        return "archive" in self.body_classes or "blog" in self.body_classes


# ---------------------------------------------------------------- page


def analyze(url: str, html: str) -> Page:
    doc = dom.parse(html)
    body = doc.find(lambda n: n.tag == "body")
    html_el = doc.find(lambda n: n.tag == "html")
    og = {}
    for meta in doc.find_all(lambda n: n.tag == "meta"):
        key = (meta.get("property") or meta.get("name")).lower()
        if key and key not in og and meta.get("content"):
            og[key] = dom.squash(meta.get("content"))
    canonical_el = doc.find(lambda n: n.tag == "link" and "canonical" in n.get("rel").lower().split())
    canonical = absolute(url, canonical_el.get("href")) if canonical_el is not None else None
    title_el = doc.find(lambda n: n.tag == "title")
    raw = html if len(html) < 400_000 else html[:400_000]
    return Page(
        url=url,
        doc=doc,
        body_classes=tuple(body.classes) if body is not None else (),
        articles=_articles(doc),
        title=title_el.clean_text() if title_el is not None else "",
        site_name=_site_name(doc, og, title_el),
        og=og,
        canonical=canonical,
        language=(html_el.get("lang") if html_el is not None else "") or None,
        wordpress=bool(re.search(r"wp-content|wp-includes|api\.w\.org|wordpress", raw, re.I)),
    )


def _articles(doc: Node) -> List[Node]:
    def is_article(node: Node) -> bool:
        if node.tag == "article":
            return True
        if POST_ID.match(node.get("id")) and node.tag in ("div", "section", "main"):
            return True
        return "hentry" in node.classes and node.tag in ("div", "section")

    found = doc.find_all(is_article)
    # Keep the outermost only: themes nest a `.hentry` div inside `<article>` routinely.
    top = [node for node in found if not any(other is not node and other.contains(node) for other in found)]
    # A sidebar "recent posts" widget can be built from <article> too; it has no story text.
    return [node for node in top if not _in_junk(node) and content_of(node) is not None]


def _site_name(doc: Node, og: dict, title_el: Optional[Node]) -> str:
    if og.get("og:site_name"):
        return og["og:site_name"]
    for selector in ("site-title", "site-branding"):
        node = doc.find(lambda n, s=selector: n.has_class(s) or n.get("id") == s)
        if node is not None and node.clean_text():
            return node.clean_text()
    node = doc.find(lambda n: n.get("id") == "site-title")
    if node is not None and node.clean_text():
        return node.clean_text()
    if title_el is not None:
        parts = re.split(r"\s+[|–—-]\s+", title_el.clean_text())
        if parts:
            return parts[-1]
    return ""


def content_of(article: Node) -> Optional[Node]:
    for name in CONTENT_CLASSES:
        found = article.find(lambda n, c=name: c in n.classes)
        if found is not None:
            return found
    return None


def _junk(node: Node) -> bool:
    if node.tag in ("body", "html", "#document"):
        return False  # body classes describe the page ("jetpack-reblog-enabled"), not furniture
    if node.tag in JUNK_TAGS:
        return True
    ident = node.get("id").lower()
    if ident in JUNK_IDS:
        return True
    for token in node.classes:
        token = token.lower()
        if token.startswith(JUNK_CLASS_PREFIXES):
            return True
    return False


def _in_junk(node: Node) -> bool:
    return _junk(node) or any(_junk(a) for a in node.ancestors())


# ---------------------------------------------------------------- chapters


def chapters(page: Page) -> List[Chapter]:
    """Every story article on the page. One for a post; several for an archive that shows
    full posts (WordPress.com serials often link the TOC to per-chapter categories)."""
    found = []
    for article in page.articles:
        chapter = _chapter(page, article, single=len(page.articles) == 1)
        if chapter is not None:
            found.append(chapter)
    if page.is_archive and len(found) > 1:
        # Archives list newest first; a story reads oldest first.
        found.sort(key=lambda c: c.published or "")
    return found


def is_post(page: Page) -> bool:
    """A single post holding one article — the kind of page "Next Chapter" points at. Pages
    (as opposed to posts) are not counted: authors use them for landing pages and tables of
    contents. A page *linked from* a table of contents can still be a chapter; see
    `accepts_as_chapter`."""
    if len(page.articles) != 1:
        return False
    classes = set(page.body_classes)
    if classes & {"single", "single-post"} or any(c.startswith("postid-") for c in classes):
        return True
    # A one-post category archive, which is what Worm's table of contents links to.
    return page.is_archive


def accepts_as_chapter(page: Page) -> bool:
    """Whether a page reached from a table of contents holds story text."""
    if not page.articles or page.is_front_page:
        return False
    if is_post(page) or (page.is_archive and len(page.articles) > 1):
        return True
    if len(page.articles) != 1:
        return False
    content = content_of(page.articles[0])
    if content is None or looks_like_index(content):
        return False
    # A WordPress *page* that links onward to other posts on the site is a landing page or a
    # sub-index (Sunspot's "Public Side Stories"), however much prose it has around the links.
    return _onward_links(content, page.url) < 2


def _onward_links(content: Node, base: str) -> int:
    count = 0
    for anchor in content.find_all(lambda n: n.tag == "a" and n.get("href")):
        url = absolute(base, anchor.get("href"))
        label = anchor.clean_text() or anchor.get("title")
        if (url and same_site(url, base) and is_candidate(url, base) and not _is_site_root(url)
                and normalize(url) != normalize(base) and not NAV_ONLY_TEXT.match(label or "")):
            count += 1
    return count


def _chapter(page: Page, article: Node, single: bool) -> Optional[Chapter]:
    content = content_of(article)
    if content is None:
        return None
    url = page.url
    permalink = None
    title_el = article.find(lambda n: "entry-title" in n.classes or "post-title" in n.classes)
    if title_el is not None:
        anchor = title_el.find(lambda n: n.tag == "a" and n.get("href"))
        if anchor is not None:
            permalink = absolute(page.url, anchor.get("href"))
    if not single and permalink:
        url = permalink
    title = title_el.clean_text() if title_el is not None else ""
    if not title and single:
        title = _strip_site(page.og.get("og:title") or page.title, page.site_name)
    post_id = _post_id(article) or (_body_post_id(page) if single else None)
    key = f"{bare_host(page.url)}#{post_id}" if post_id else normalize(permalink or (page.canonical if single else None) or url)

    locked = None
    form_action = None
    form = article.find(lambda n: n.tag == "form" and ("post-password-form" in n.classes
                                                       or "postpass" in n.get("action")))
    if "post-password-required" in article.classes or form is not None:
        locked = "password"
        if form is not None and form.get("action"):
            form_action = absolute(page.url, form.get("action"))
        elif page.wordpress:
            form_action = urllib.parse.urljoin(page.url, "/wp-login.php?action=postpass")
    elif _patreon_locked(content):
        locked = "patreon"

    return Chapter(
        url=url,
        key=key,
        title=PROTECTED_PREFIX.sub("", title).strip() or title,
        content=content,
        article=article,
        published=_published(article, page if single else None),
        author=_author(article),
        categories=tuple(sorted(c[9:] for c in article.classes if c.startswith("category-"))),
        locked=locked,
        password_form=form_action,
    )


def _post_id(article: Node) -> Optional[str]:
    match = POST_ID.match(article.get("id"))
    if match:
        return match.group(1)
    for token in article.classes:
        match = POST_ID.match(token)
        if match:
            return match.group(1)
    return None


def _body_post_id(page: Page) -> Optional[str]:
    for token in page.body_classes:
        match = POSTID_CLASS.match(token)
        if match:
            return match.group(1)
    return None


def _published(article: Node, page: Optional[Page]) -> Optional[str]:
    candidates = []
    for time_el in article.find_all(lambda n: n.tag == "time" and n.get("datetime")):
        weight = 0 if ("entry-date" in time_el.classes or "published" in time_el.classes) else 1
        candidates.append((weight, time_el.get("datetime")))
    candidates.sort(key=lambda pair: pair[0])
    if page is not None and page.og.get("article:published_time"):
        candidates.insert(0, (0, page.og["article:published_time"]))
    for _, value in candidates:
        stamp = rfc3339(value)
        if stamp:
            return stamp
    return None


def _author(article: Node) -> Optional[str]:
    node = article.find(lambda n: n.tag == "a" and "author" in n.get("rel").lower().split())
    if node is None:
        node = article.find(lambda n: "author" in n.classes or "byline" in n.classes)
    text = node.clean_text() if node is not None else ""
    text = re.sub(r"^\s*by\s+", "", text, flags=re.I)
    return text or None


def _patreon_locked(content: Node) -> bool:
    """Patreon-gated posts render a short "unlock with Patreon" stub instead of the text."""
    markers = content.find(lambda n: any("patreon" in c.lower() and ("lock" in c.lower() or "gate" in c.lower()
                                                                    or "content" in c.lower())
                                         for c in n.classes))
    if markers is None:
        return False
    return len(dom.squash(content.text())) < 1200


def looks_like_index(content: Node) -> bool:
    """Mostly links: a table of contents or a link roundup, not prose."""
    text = dom.squash(content.text())
    anchors = content.find_all(lambda n: n.tag == "a" and n.get("href"))
    if len(anchors) < 5:
        return False
    link_text = sum(len(a.clean_text()) for a in anchors)
    return link_text >= 0.5 * max(len(text), 1)


# ---------------------------------------------------------------- navigation


def next_link(page: Page, chapter: Chapter) -> Optional[Tuple[str, str]]:
    """(url, how) for the chapter after this one. `how` is "author" for a link the author
    placed in the post, "theme" for the theme's chronological next-post link — the latter is
    weaker evidence, because it points at the next *post*, whatever it is."""
    return _nav_link(page, chapter, NEXT_TEXT, "next", "nav-next")


def prev_link(page: Page, chapter: Chapter) -> Optional[Tuple[str, str]]:
    return _nav_link(page, chapter, PREV_TEXT, "prev", "nav-previous")


def _nav_link(page: Page, chapter: Chapter, text_re, rel: str, theme_class: str) -> Optional[Tuple[str, str]]:
    def usable(href: str) -> Optional[str]:
        url = absolute(page.url, href)
        if not url or not same_site(url, page.url) or not is_candidate(url, chapter.url):
            return None
        return url

    scopes = []
    if chapter.content is not None:
        scopes.append(("author", chapter.content))
    if chapter.article is not None:
        scopes.append(("author", chapter.article))
    for how, scope in scopes:
        for anchor in scope.find_all(lambda n: n.tag == "a" and n.get("href")):
            label = anchor.clean_text() or anchor.get("title")
            rels = anchor.get("rel").lower().split()
            if text_re.match(label or "") or rel in rels:
                url = usable(anchor.get("href"))
                if url:
                    # The block editor's post-navigation block is the theme's link wearing
                    # the post's clothes: chronological, not authored.
                    themed = any(_theme_nav(a) for a in anchor.ancestors() if scope.contains(a) or a is scope)
                    return url, ("theme" if themed else how)

    # The theme's own post navigation, outside the article.
    for anchor in page.doc.find_all(lambda n: n.tag == "a" and n.get("href")):
        rels = anchor.get("rel").lower().split()
        chain = [anchor, *anchor.ancestors()]
        in_nav = any(_theme_nav(a) for a in chain)
        label = anchor.clean_text() or anchor.get("title")
        if rel in rels or (in_nav and (text_re.match(label or "") or any(theme_class in a.classes for a in chain))):
            url = usable(anchor.get("href"))
            if url:
                return url, "theme"
    for link_el in page.doc.find_all(lambda n: n.tag == "link" and rel in n.get("rel").lower().split()):
        url = usable(link_el.get("href"))
        if url:
            return url, "theme"
    return None


def _theme_nav(node: Node) -> bool:
    ident = node.get("id").lower()
    if ident in ("nav-single", "nav-below", "nav-above") or node.tag == "nav":
        return True
    return any(c.startswith(("post-navigation", "nav-links", "wp-block-post-navigation", "navigation"))
               for c in node.classes)


# ---------------------------------------------------------------- tables of contents


def main_region(page: Page) -> Node:
    """Where an index's links live: the single article's content if there is one, else the
    theme's content column, else the body."""
    if len(page.articles) == 1:
        content = content_of(page.articles[0])
        if content is not None:
            return content
    for ident in ("content", "primary", "main", "main-content", "site-content"):
        node = page.doc.find(lambda n, i=ident: n.get("id") == i)
        if node is not None:
            return node
    node = page.doc.find(lambda n: n.tag == "main")
    if node is not None:
        return node
    return page.doc.find(lambda n: n.tag == "body") or page.doc


def toc_links(page: Page, region: Optional[Node] = None) -> List[Link]:
    """Same-site links in the page's main region that could be chapters, in document order,
    each URL once."""
    region = region or main_region(page)
    seen, links = set(), []
    self_url = normalize(page.url)
    canonical = normalize(page.canonical) if page.canonical else None
    for anchor in region.find_all(lambda n: n.tag == "a" and n.get("href")):
        if _in_junk_within(anchor, region):
            continue
        url = absolute(page.url, anchor.get("href"))
        if not url or not same_site(url, page.url) or not is_candidate(url, page.url):
            continue
        norm = normalize(url)
        if norm in (self_url, canonical) or norm in seen or _is_site_root(url):
            continue
        text = anchor.clean_text() or anchor.get("title")
        if NAV_ONLY_TEXT.match(text or "") and not re.search(r"\d", text or ""):
            continue  # "Home", "Next", "Table of Contents" inside a TOC page are not chapters
        seen.add(norm)
        links.append(Link(url=url, text=text, rel=anchor.get("rel"), node=anchor))
    return links


def _in_junk_within(node: Node, region: Node) -> bool:
    if _junk(node):
        return True
    for ancestor in node.ancestors():
        if ancestor is region:
            return False
        if _junk(ancestor):
            return True
    return False


def is_toc_page(page: Page, links: List[Link]) -> bool:
    """Does this page present itself as the index?"""
    heading = ""
    if len(page.articles) == 1:
        title_el = page.articles[0].find(lambda n: "entry-title" in n.classes)
        heading = title_el.clean_text() if title_el is not None else ""
    heading = heading or _strip_site(page.og.get("og:title") or page.title, page.site_name)
    if TOC_TITLE.match(heading) or (heading and TOC_SUFFIX.search(heading) and links):
        return True
    if any("table_of_contents" in c or "table-of-contents" in c for c in page.body_classes):
        return True
    return len(links) >= 20


def find_toc_link(page: Page) -> Optional[str]:
    """A link to the table of contents: in the story column first, then anywhere (menus)."""
    here = normalize(page.url)
    for region, skip_junk in ((main_region(page), True), (page.doc, False)):
        best = None
        for anchor in region.find_all(lambda n: n.tag == "a" and n.get("href")):
            if skip_junk and _in_junk_within(anchor, region):
                continue
            text = anchor.clean_text() or anchor.get("title")
            if not text or not TOC_TEXT.search(text) or re.search(r"\b(?:next|previous|latest)\b", text, re.I):
                continue
            url = absolute(page.url, anchor.get("href"))
            if not url or not same_site(url, page.url) or normalize(url) == here or not is_candidate(url, page.url):
                continue
            rank = 0 if re.search(r"table\s+of\s+contents", text, re.I) else 1
            if best is None or rank < best[0]:
                best = (rank, url)
                if rank == 0:
                    break
        if best:
            return best[1]
    return None


def find_start_link(page: Page) -> Optional[str]:
    here = normalize(page.url)
    for region in (main_region(page), page.doc):
        for anchor in region.find_all(lambda n: n.tag == "a" and n.get("href")):
            text = anchor.clean_text() or anchor.get("title")
            if not text or not START_TEXT.search(text):
                continue
            url = absolute(page.url, anchor.get("href"))
            if url and same_site(url, page.url) and normalize(url) != here and is_candidate(url, page.url):
                return url
    return None


# ---------------------------------------------------------------- serial-level metadata


def serial_title(start: Page, toc: Optional[Page]) -> str:
    for page in (start, toc):
        if page is None or page.is_front_page or is_post(page):
            continue
        heading = ""
        if len(page.articles) == 1:
            title_el = page.articles[0].find(lambda n: "entry-title" in n.classes)
            heading = title_el.clean_text() if title_el is not None else ""
        heading = heading or _strip_site(page.og.get("og:title") or page.title, page.site_name)
        heading = TOC_SUFFIX.sub("", heading).strip(" -–—:|")
        if heading and not TOC_TITLE.match(heading):
            return heading
    return start.site_name or (toc.site_name if toc else "") or bare_host(start.url)


def description(page: Page) -> Optional[str]:
    text = page.og.get("og:description") or page.og.get("description")
    if text and not re.match(r"^(?:visit the post for more|\[?…\]?)$", text, re.I):
        return text
    return None


def cover_url(page: Page) -> Optional[str]:
    image = page.og.get("og:image") or page.og.get("twitter:image")
    if image and not re.search(r"blank\.jpg|/i/webclip|gravatar|blavatar|site-?icon|cropped-.*-32x32|"
                               r"wp-content/plugins/|s0\.wp\.com/i/", image, re.I):
        return absolute(page.url, image)
    if is_post(page):
        return None
    region = main_region(page)
    for img in region.find_all(lambda n: n.tag == "img"):
        src = img.get("data-lazy-src") or img.get("data-src") or img.get("src")
        width = _int(img.get("width"))
        height = _int(img.get("height"))
        if src and not src.startswith("data:") and (width is None or width >= 200) and (height is None or height >= 250):
            return absolute(page.url, src)
    return None


def _int(value: str) -> Optional[int]:
    try:
        return int(float(value))
    except (TypeError, ValueError):
        return None


# ---------------------------------------------------------------- URLs


def absolute(base: str, href: Optional[str]) -> Optional[str]:
    if not href:
        return None
    href = href.strip()
    if href.startswith(("javascript:", "mailto:", "tel:", "data:", "#")):
        return None
    try:
        url = urllib.parse.urljoin(base, href)
    except ValueError:
        return None
    parts = split(url)
    if parts is None or parts.scheme not in ("http", "https") or not parts.hostname:
        return None
    return urllib.parse.urlunsplit((parts.scheme, parts.netloc, parts.path or "/", parts.query, ""))


def normalize(url: Optional[str]) -> str:
    """Identity of a URL for de-duplication: host without www, no scheme, no trailing slash,
    no fragment, query kept only where WordPress puts post identity in it."""
    parts = split(url) if url else None
    if parts is None:
        return (url or "").strip().lower()
    host = bare_host(parts.hostname or "")
    path = re.sub(r"/+$", "", parts.path) or ""
    keep = [(k, v) for k, v in urllib.parse.parse_qsl(parts.query) if k in ("p", "page_id", "cat")]
    query = ("?" + urllib.parse.urlencode(sorted(keep))) if keep else ""
    return f"{host}{path}{query}".lower()


def same_site(a: str, b: str) -> bool:
    return bare_host(a) == bare_host(b) and bare_host(a) != ""


def is_candidate(url: str, base: str) -> bool:
    parts = split(url)
    if parts is None or parts.scheme not in ("http", "https"):
        return False
    if NON_CHAPTER_PATH.search(parts.path):
        return False
    if parts.query and NON_CHAPTER_QUERY.search(parts.query):
        return False
    if re.search(r"patreon|discord|ko-?fi|subscribe|login|signup|register|cart|checkout|donate", parts.path, re.I):
        return False
    return True


def _is_site_root(url: str) -> bool:
    parts = split(url)
    return parts is not None and parts.path in ("", "/") and not parts.query


def _strip_site(title: str, site: str) -> str:
    title = title or ""
    if site:
        title = re.sub(r"\s*[|–—-]\s*" + re.escape(site) + r"\s*$", "", title)
    return dom.squash(title)


def rfc3339(value: Optional[str]) -> Optional[str]:
    if not value:
        return None
    text = value.strip().replace("Z", "+00:00")
    try:
        stamp = datetime.fromisoformat(text)
    except ValueError:
        return None
    if stamp.tzinfo is None:
        stamp = stamp.replace(tzinfo=timezone.utc)
    return stamp.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def first_author(chapters_: Iterable[Chapter]) -> Optional[str]:
    for chapter in chapters_:
        if chapter.author:
            return chapter.author
    return None


def first_author_name(page: Page) -> Optional[str]:
    """The page's declared author, when it is a name and not a profile URL."""
    for key in ("author", "article:author", "twitter:data1"):
        value = page.og.get(key)
        if value and not re.match(r"^https?://", value) and len(value) <= 80:
            return value
    return None
