"""The whole crawl against a fake site: TOC, redirects, tail-walking, locks, cache, images."""

import os
import shutil
import tempfile
import unittest
import zipfile
from xml.etree import ElementTree

from app import crawl, epub
from app.cache import Cache
from app.routing import probe_url

from .helpers import FakeFetcher, post

BASE = "https://serial.example.org"
PNG = b"\x89PNG\r\n\x1a\n" + b"\x00" * 64


def landing() -> str:
    return f"""<html><head><title>The Serial | Serial</title><meta property="og:site_name" content="Serial"/>
<meta property="og:image" content="{BASE}/wp-content/uploads/cover.png"/><link rel="https://api.w.org/" href="/wp-json/"/></head>
<body class="page page-id-2"><div id="content"><article id="post-2" class="page hentry">
<h1 class="entry-title">The Serial</h1><div class="entry-content"><p>A story.</p>
<p><a href="{BASE}/toc/">Table of Contents</a></p></div></article></div></body></html>"""


def toc() -> str:
    return f"""<html><head><title>Table of Contents | Serial</title><link rel="https://api.w.org/" href="/wp-json/"/></head>
<body class="page page-id-3"><div id="content"><article id="post-3" class="page hentry">
<h1 class="entry-title">Table of Contents</h1><div class="entry-content"><ul>
<li><a href="{BASE}/old-slug-1/">1</a></li><li><a href="{BASE}/missing/">lost</a></li>
<li><a href="{BASE}/extras/">Extras</a></li><li><a href="{BASE}/chapter-2/">2</a></li>
<li><a href="{BASE}/?p=11">1 again, by id</a></li></ul></div></article></div></body></html>"""


def extras() -> str:
    return f"""<html><head><link rel="https://api.w.org/" href="/wp-json/"/></head><body class="page page-id-9">
<article id="post-9" class="page hentry"><h1 class="entry-title">Extras</h1><div class="entry-content">
<p>Bonus stories, not part of the main line, for anyone who wants more of the cast.</p>
<p><a href="{BASE}/bonus-1/">Bonus one</a> and <a href="{BASE}/bonus-2/">Bonus two</a>.</p></div></article></body></html>"""


def site_pages(chapter3_next="author"):
    c3_kwargs = {"nxt": f"{BASE}/chapter-4/"} if chapter3_next == "author" else {"theme_next": f"{BASE}/news/"}
    return {
        f"{BASE}/story/": landing(),
        f"{BASE}/toc/": toc(),
        f"{BASE}/extras/": extras(),
        f"{BASE}/old-slug-1/": (f"{BASE}/chapter-1/", post(
            11, "The Serial – Chapter 1",
            f'<p>One.</p><img src="{BASE}/wp-content/uploads/map.png" alt="map"/>',
            nxt=f"{BASE}/chapter-2/", date="2025-01-01T00:00:00+00:00")),
        f"{BASE}/?p=11": (f"{BASE}/chapter-1/", post(11, "The Serial – Chapter 1", "<p>One.</p>")),
        f"{BASE}/chapter-2/": post(12, "The Serial – Chapter 2", "<p>Two.</p>", prev=f"{BASE}/chapter-1/",
                                  nxt=f"{BASE}/chapter-3/", date="2025-02-01T00:00:00+00:00"),
        f"{BASE}/chapter-3/": post(13, "Chapter 3", "<p>Three.</p>", prev=f"{BASE}/chapter-2/",
                                  date="2025-03-01T00:00:00+00:00", **c3_kwargs),
        f"{BASE}/chapter-4/": post(14, "Chapter 4", "", protected=True, theme_next=f"{BASE}/chapter-5/"),
        f"{BASE}/news/": post(20, "Site news", "<p>Hiatus.</p>", categories=("news",)),
        f"{BASE}/wp-content/uploads/map.png": PNG,
        f"{BASE}/wp-content/uploads/cover.png": PNG,
    }


class CrawlTestCase(unittest.TestCase):
    def setUp(self):
        self.workdir = tempfile.mkdtemp(prefix="wpserial-test-")
        self.cache = Cache(self.workdir)
        self.events = []

    def tearDown(self):
        shutil.rmtree(self.workdir, ignore_errors=True)

    def crawl(self, fetcher, url=f"{BASE}/story/", previous=None):
        crawler = crawl.Crawler(fetcher, self.cache, self.events.append)
        return crawler.run(url, previous_urls=previous)

    def logs(self):
        return [e["message"] for e in self.events if e["event"] == "log"]


class TableOfContentsCrawl(CrawlTestCase):
    def test_reads_the_toc_then_follows_next_past_its_end(self):
        book = self.crawl(FakeFetcher(site_pages()))
        self.assertEqual("The Serial", book.title)
        self.assertEqual(["Chapter 1", "Chapter 2", "Chapter 3"], [c.title for c in book.chapters],
                         "series name stripped from titles; chapter 3 found via Next; the ?p= duplicate dropped")
        self.assertEqual(["serial.example.org#11", "serial.example.org#12", "serial.example.org#13"],
                         [c.key for c in book.chapters])
        self.assertEqual("Author Person", book.author)
        self.assertEqual("2025-01-01T00:00:00Z", book.published)
        self.assertEqual("2025-03-01T00:00:00Z", book.updated)
        self.assertEqual(2, book.skipped, "the 404 and the Extras index are skipped, not fatal")
        self.assertTrue(any("not a chapter, skipped" in m and "/extras/" in m for m in self.logs()))
        self.assertTrue(any("locked chapter (password): Chapter 4" in m for m in self.logs()))
        self.assertIsNotNone(book.cover)
        self.assertEqual(1, len(book.images))
        self.assertNotIn("Next Chapter", book.chapters[1].body)

    def test_a_password_opens_protected_chapters(self):
        pages = site_pages()
        unlocked = {f"{BASE}/chapter-4/": post(14, "Chapter 4", "<p>Four, unlocked.</p>")}
        book = self.crawl(FakeFetcher(pages, password="right", unlocked=unlocked))
        self.assertEqual("Chapter 4", book.chapters[-1].title)
        self.assertIn("Four, unlocked.", book.chapters[-1].body)

    def test_a_wrong_password_is_reported_and_tried_once(self):
        book = self.crawl(FakeFetcher(site_pages(), password="wrong"))
        self.assertEqual(3, len(book.chapters))
        self.assertTrue(any("was not accepted" in m for m in self.logs()))

    def test_the_theme_next_link_stops_at_a_different_category(self):
        book = self.crawl(FakeFetcher(site_pages(chapter3_next="theme")))
        self.assertEqual(3, len(book.chapters))
        self.assertTrue(any("different category" in m for m in self.logs()))

    def test_an_update_rereads_only_the_newest_chapters(self):
        first = FakeFetcher(site_pages())
        book = self.crawl(first)
        second = FakeFetcher(site_pages())
        self.crawl(second, previous=[c.url for c in book.chapters])
        self.assertNotIn(f"{BASE}/old-slug-1/", second.fetched, "chapter 1 comes from the cache")
        self.assertNotIn(f"{BASE}/wp-content/uploads/map.png", second.fetched, "so does its image")
        self.assertIn(f"{BASE}/chapter-3/", second.fetched, "the newest chapter is always re-read")
        self.assertLess(second.requests, first.requests)

    def test_fingerprint_moves_only_when_the_book_would(self):
        a = self.crawl(FakeFetcher(site_pages()))
        b = self.crawl(FakeFetcher(site_pages()))
        self.assertEqual(crawl.fingerprint(a), crawl.fingerprint(b))
        pages = site_pages()
        pages[f"{BASE}/chapter-3/"] = post(13, "Chapter 3", "<p>Three, revised.</p>", prev=f"{BASE}/chapter-2/")
        c = self.crawl(FakeFetcher(pages), previous=[ch.url for ch in a.chapters])
        self.assertNotEqual(crawl.fingerprint(a), crawl.fingerprint(c))


class NoTableOfContents(CrawlTestCase):
    def test_a_pasted_chapter_walks_back_to_the_start_then_forward(self):
        pages = site_pages()
        pages[f"{BASE}/chapter-1/"] = pages[f"{BASE}/old-slug-1/"][1]
        book = self.crawl(FakeFetcher(pages), url=f"{BASE}/chapter-2/")
        self.assertEqual(["serial.example.org#11", "serial.example.org#12", "serial.example.org#13"],
                         [c.key for c in book.chapters])
        self.assertEqual("Serial", book.title, "with no landing page, the serial is named after the site")

    def test_a_site_that_is_not_wordpress_is_unsupported(self):
        fetcher = FakeFetcher({"https://plain.example.org/": "<html><body><p>hello</p></body></html>"})
        with self.assertRaises(crawl.CrawlError) as raised:
            self.crawl(fetcher, url="https://plain.example.org/")
        self.assertEqual("unsupported_url", raised.exception.code)

    def test_a_missing_start_page_is_not_found(self):
        with self.assertRaises(crawl.CrawlError) as raised:
            self.crawl(FakeFetcher({}), url=f"{BASE}/nope/")
        self.assertEqual("not_found", raised.exception.code)


class Binding(CrawlTestCase):
    def test_the_epub_is_valid_and_carries_its_source(self):
        book = self.crawl(FakeFetcher(site_pages()))
        path = os.path.join(self.workdir, "book.epub")
        fallbacks = epub.build(path, title=book.title, authors=[book.author], language=book.language,
                               identifier=epub.stable_uuid(book.source_id),
                               chapters=[epub.Chapter(c.title, c.body) for c in book.chapters],
                               images=book.images, cover=book.cover, source_url=book.source_url,
                               published=book.published, modified=book.updated)
        self.assertEqual(0, fallbacks)
        with zipfile.ZipFile(path) as z:
            first = z.infolist()[0]
            self.assertEqual("mimetype", first.filename)
            self.assertEqual(zipfile.ZIP_STORED, first.compress_type)
            for name in z.namelist():
                if name.endswith((".xhtml", ".opf", ".ncx", ".xml")):
                    text = z.read(name).decode("utf-8").replace("<!DOCTYPE html>", "")
                    ElementTree.fromstring(text.encode("utf-8"))
            opf = z.read("OEBPS/content.opf").decode("utf-8")
            self.assertIn(f"<dc:source>{BASE}/story/</dc:source>", opf)
            self.assertIn('property="dcterms:modified"', opf)
            image_names = [n for n in z.namelist() if n.startswith("OEBPS/images/")]
            self.assertEqual(1, len(image_names))
            chapter1 = z.read("OEBPS/chap0001.xhtml").decode("utf-8")
            self.assertIn(f'src="{image_names[0][len("OEBPS/"):]}"', chapter1)

    def test_a_chapter_with_broken_markup_falls_back_to_text(self):
        path = os.path.join(self.workdir, "b.epub")
        fallbacks = epub.build(path, title="T", authors=[], language="en", identifier="x",
                               chapters=[epub.Chapter("One", "<p>fine</p><p>broken<b></p>")], images={})
        self.assertEqual(1, fallbacks)


class Probe(unittest.TestCase):
    def test_claims_known_and_wordpress_shaped_urls(self):
        self.assertTrue(probe_url("https://parahumans.wordpress.com/2011/06/11/1-1/")["supported"])
        self.assertTrue(probe_url("https://sunspot.gay/")["supported"], "must agree with the manifest's matches")
        self.assertTrue(probe_url("https://hungryshedgerow.net/maidens-of-the-fall/")["supported"])
        self.assertTrue(probe_url("https://someserial.net/?p=123")["supported"])
        self.assertTrue(probe_url("https://someserial.net/2020/01/02/chapter-one/")["supported"])
        self.assertTrue(probe_url("https://someserial.net/", known_hosts=["someserial.net"])["supported"])

    def test_declines_the_rest(self):
        for url in ("https://conformance.invalid/definitely-not-a-work/0", "https://someserial.net/",
                    "http://localhost/?p=1", "ftp://e.org/?p=1", "https://10.0.0.1/?p=1",
                    "https://sunspot.gay[/#]", "http://[broken/?p=1", "https://e.org:99999/?p=1", ""):
            self.assertFalse(probe_url(url)["supported"], repr(url))


class MalformedUrls(CrawlTestCase):
    def test_one_bad_href_does_not_abort_the_crawl(self):
        pages = site_pages()
        pages[f"{BASE}/chapter-2/"] = post(
            12, "The Serial – Chapter 2",
            '<p>Two, with <a href="http://[broken">a bad link</a> and <a href="https://e.org:99999/">another</a>.</p>'
            '<img src="http://[nope/x.png"/>',
            nxt=f"{BASE}/chapter-3/")
        book = self.crawl(FakeFetcher(pages))
        self.assertEqual(3, len(book.chapters))
        self.assertIn("Two, with a bad link and another.", book.chapters[1].body)

    def test_a_malformed_start_url_is_an_error_not_a_crash(self):
        with self.assertRaises(crawl.CrawlError) as raised:
            self.crawl(FakeFetcher({}), url="http://[broken/")
        self.assertIn(raised.exception.code, ("unsupported_url", "not_found"))


if __name__ == "__main__":
    unittest.main()
