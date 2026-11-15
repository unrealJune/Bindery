"""Page reading against markup shaped like Worm, Maidens of the Fall, and Sunspot."""

import unittest

from app import dom, site

from .helpers import fixture

WORM_TOC = "https://parahumans.wordpress.com/table-of-contents/"
WORM_102 = "https://parahumans.wordpress.com/category/stories-arcs-1-10/arc-1-gestation/1-02/"
HEDGEROW = "https://hungryshedgerow.net/maidens-of-the-fall/"
HEDGEROW_TOC = "https://hungryshedgerow.net/maidens-of-the-fall-chapters/"
HEDGEROW_11 = "https://hungryshedgerow.net/motf-disarticulation-1-1/"
HEDGEROW_71 = "https://hungryshedgerow.net/maidens-of-the-fall-front-toward-enemy-7-1/"
SUNSPOT = "https://sunspot.gay/"
SUNSPOT_TOC = "https://sunspot.gay/table-of-contents/"
SUNSPOT_101 = "https://sunspot.gay/chapter/from-on-high-1-01/"


def page(url, name):
    return site.analyze(url, fixture(name))


class TreeBuilding(unittest.TestCase):
    def test_an_unclosed_paragraph_ends_at_the_next_block(self):
        root = dom.parse("<div><p>one<p>two<div>three</div></div>")
        paragraphs = root.find_all(lambda n: n.tag == "p")
        self.assertEqual(["one", "two"], [p.text() for p in paragraphs])
        self.assertEqual("div", root.find(lambda n: n.text() == "three").tag)

    def test_stray_end_tags_and_void_elements_do_not_derail_the_tree(self):
        root = dom.parse("<p>a<br>b</span></p><img src=x><p>c</p>")
        self.assertEqual(["a\nb", "c"], [p.text() for p in root.find_all(lambda n: n.tag == "p")])

    def test_list_items_close_each_other(self):
        root = dom.parse("<ul><li>one<li>two</ul>")
        self.assertEqual(2, len(root.find_all(lambda n: n.tag == "li")))
        self.assertTrue(all(li.parent.tag == "ul" for li in root.find_all(lambda n: n.tag == "li")))


class FindingTheTableOfContents(unittest.TestCase):
    def test_worm_toc_is_recognised_and_lists_each_chapter_once(self):
        toc = page(WORM_TOC, "worm_toc.html")
        links = site.toc_links(toc)
        self.assertTrue(site.is_toc_page(toc, links))
        self.assertEqual(
            ["1.01", "1.02", "1.x (Interlude; Danny)", "E.2"], [l.text for l in links],
            "the empty duplicate 1.02 link, the share button, and related posts are not chapters")

    def test_hedgerow_landing_page_links_to_its_chapter_list(self):
        landing = page(HEDGEROW, "hedgerow_landing.html")
        self.assertFalse(site.is_post(landing), "a WordPress page is a landing page, not a chapter")
        self.assertEqual(HEDGEROW_TOC, site.find_toc_link(landing))
        self.assertEqual("Maidens of the Fall", site.serial_title(landing, None))
        self.assertTrue(site.cover_url(landing).endswith("Octavia-640x1024.jpg"))

    def test_hedgerow_chapter_list_page(self):
        toc = page(HEDGEROW_TOC, "hedgerow_toc.html")
        self.assertEqual(["1.1", "1.2"], [l.text for l in site.toc_links(toc)])

    def test_sunspot_home_finds_the_toc_in_the_menu_over_start_reading(self):
        home = page(SUNSPOT, "sunspot_home.html")
        self.assertTrue(home.is_front_page)
        self.assertEqual(SUNSPOT_TOC, site.find_toc_link(home))
        self.assertEqual(SUNSPOT_101, site.find_start_link(home))
        self.assertEqual("Sunspot", site.serial_title(home, None))
        self.assertEqual("A web serial about glyphs, girlcreatures, and gender.", site.description(home))

    def test_sunspot_toc_without_an_article_uses_the_content_column(self):
        toc = page(SUNSPOT_TOC, "sunspot_toc.html")
        links = site.toc_links(toc)
        self.assertTrue(site.is_toc_page(toc, links))
        urls = [l.url for l in links]
        self.assertIn("https://sunspot.gay/?p=45", urls)
        self.assertIn("https://sunspot.gay/?p=84", urls, "author's notes listed in the TOC are kept")
        self.assertNotIn("https://sunspot.gay/?p=1490", urls, "the sidebar is not the TOC")
        self.assertEqual("https://sunspot.gay/table-of-contents/side-stories", urls[0],
                         "kept as a candidate: it is rejected only once loaded and seen to be an index")


class ReadingChapters(unittest.TestCase):
    def test_hedgerow_chapter(self):
        p = page(HEDGEROW_11, "hedgerow_chapter.html")
        self.assertTrue(site.is_post(p))
        [chapter] = site.chapters(p)
        self.assertEqual("hungryshedgerow.net#108", chapter.key)
        self.assertEqual("Maidens of the Fall – Disarticulation – 1.1", chapter.title)
        self.assertEqual("2025-11-14T19:45:41Z", chapter.published)
        self.assertEqual("Hungry", chapter.author)
        self.assertIsNone(chapter.locked)
        self.assertEqual(("https://hungryshedgerow.net/disarticulation-1-2/", "author"), site.next_link(p, chapter),
                         "the author's in-post link wins over the theme's next-post link")

    def test_worm_one_post_category_archive_is_a_chapter_keyed_by_post_id(self):
        p = site.analyze(WORM_102, fixture("worm_chapter.html"))
        self.assertTrue(site.is_post(p), "body classes like jetpack-reblog-enabled must not hide the article")
        [chapter] = site.chapters(p)
        self.assertEqual("parahumans.wordpress.com#8", chapter.key)
        self.assertEqual("Gestation 1.2", chapter.title)
        self.assertEqual("2011-06-14T05:01:19Z", chapter.published)
        self.assertEqual(("https://parahumans.wordpress.com/2011/06/17/gestation-1-3/", "author"),
                         site.next_link(p, chapter))
        self.assertEqual(("https://parahumans.wordpress.com/2011/06/11/1-1/", "author"),
                         site.prev_link(p, chapter), '"Last Chapter" means previous')

    def test_protected_post_is_locked_and_still_navigable(self):
        p = page(HEDGEROW_71, "hedgerow_protected.html")
        [chapter] = site.chapters(p)
        self.assertEqual("password", chapter.locked)
        self.assertEqual("Maidens of the Fall – Front Toward Enemy – 7.1", chapter.title, "'Protected: ' is dropped")
        self.assertEqual("https://hungryshedgerow.net/wp-login.php?action=postpass", chapter.password_form)
        self.assertEqual(("https://hungryshedgerow.net/maidens-of-the-fall-front-toward-enemy-7-2/", "theme"),
                         site.next_link(p, chapter))

    def test_block_editor_post_navigation_counts_as_the_theme_not_the_author(self):
        p = page(SUNSPOT_101, "sunspot_chapter.html")
        [chapter] = site.chapters(p)
        self.assertEqual(("chapter",), chapter.categories)
        self.assertEqual(("https://sunspot.gay/chapter/from-on-high-1-02/", "theme"), site.next_link(p, chapter))

    def test_a_landing_page_is_not_accepted_as_a_chapter(self):
        self.assertFalse(site.accepts_as_chapter(page(SUNSPOT, "sunspot_home.html")))


class Urls(unittest.TestCase):
    def test_normalize_unifies_trivial_variants_but_keeps_post_ids(self):
        self.assertEqual(site.normalize("https://www.Example.org/a/b/#x"), site.normalize("http://example.org/a/b"))
        self.assertNotEqual(site.normalize("https://e.org/?p=1"), site.normalize("https://e.org/?p=2"))
        self.assertEqual(site.normalize("https://e.org/?p=1&utm_source=x"), site.normalize("https://e.org/?p=1"))

    def test_wordpress_plumbing_is_never_a_chapter(self):
        base = "https://e.org/"
        for url in ("https://e.org/tag/x/", "https://e.org/feed/", "https://e.org/wp-login.php",
                    "https://e.org/post/?share=twitter", "https://e.org/category/c/page/2/",
                    "https://e.org/wp-content/uploads/a.png", "https://e.org/patreon-flow/?patreon-login=yes"):
            self.assertFalse(site.is_candidate(url, base), url)
        self.assertTrue(site.is_candidate("https://e.org/category/arc-1/1-01/", base))

    def test_rfc3339(self):
        self.assertEqual("2024-06-16T04:00:15Z", site.rfc3339("2024-06-16T00:00:15-04:00"))
        self.assertIsNone(site.rfc3339("June 16"))


if __name__ == "__main__":
    unittest.main()
