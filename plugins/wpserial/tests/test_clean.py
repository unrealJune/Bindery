"""Chapter bodies: furniture out, story kept, output well-formed and script-free."""

import unittest
from xml.etree import ElementTree

from app import clean, site

from .helpers import fixture

HEDGEROW_11 = "https://hungryshedgerow.net/motf-disarticulation-1-1/"
SUNSPOT_101 = "https://sunspot.gay/chapter/from-on-high-1-01/"
WORM_102 = "https://parahumans.wordpress.com/category/stories-arcs-1-10/arc-1-gestation/1-02/"


def body_of(url, name, embed=True):
    page = site.analyze(url, fixture(name))
    [chapter] = site.chapters(page)
    clean.strip_furniture(chapter.content, page.url)
    return clean.serialize(chapter.content, page.url, embed), clean.image_urls(chapter.content, page.url)


def well_formed(fragment):
    ElementTree.fromstring(f'<body xmlns="http://www.w3.org/1999/xhtml">{fragment}</body>')


class Furniture(unittest.TestCase):
    def test_hedgerow_navigation_and_sharing_are_removed_and_prose_kept(self):
        body, _ = body_of(HEDGEROW_11, "hedgerow_chapter.html")
        well_formed(body)
        self.assertNotIn("Next Chapter", body)
        self.assertNotIn("Share on X", body)
        self.assertIn("<p>Magical girls do not impress me.</p>", body)
        self.assertIn("the sky &amp; the <em>battle</em>", body)
        self.assertIn("Willow has bundled herself on top of me.<br/>She has taken my share.", body,
                      "an unclosed <p> still ends up as one paragraph")
        self.assertIn("head over to my patreon page and subscribe", body,
                      "a promo link inside a sentence loses the link, not the words")
        self.assertNotIn("patreon.com", body)
        self.assertNotIn("<br/>\n<br/>\n<br/>", body, "spacer breaks left by removed navigation collapse")
        self.assertTrue(body.startswith("<div><p><strong>Content Warnings</strong></p>"),
                        "<details>/<summary> become plain blocks an ereader can show")

    def test_worm_last_and_next_links_go_including_the_separator(self):
        body, _ = body_of(WORM_102, "worm_chapter.html")
        well_formed(body)
        self.assertEqual('<p dir="ltr">I was going to be a superhero.</p>\n'
                         '<p dir="ltr">That was the goal I used to calm myself down.</p>', body)

    def test_sunspot_boilerplate_buttons_and_post_navigation_are_removed(self):
        body, images = body_of(SUNSPOT_101, "sunspot_chapter.html")
        well_formed(body)
        self.assertNotIn("Join the Patreon", body)
        self.assertNotIn("discord", body.lower())
        self.assertNotIn("From On High // 1.02", body)
        self.assertIn("SPAM THIS \U0001F333 TO HELP BRI", body)
        self.assertEqual([
            "https://sunspot.gay/wp-content/themes/sunspot/media/icons/flame_white.png",
            "https://sunspot.gay/wp-content/uploads/2025/05/alice-poster-1082x1536.png",
        ], images, "the largest srcset size that fits a screen, not the 2560px original")


class Safety(unittest.TestCase):
    def test_no_script_handler_or_javascript_url_survives(self):
        body, _ = body_of(SUNSPOT_101, "sunspot_chapter.html")
        lowered = body.lower()
        for needle in ("<script", "alert(", "onclick", "javascript:", "steal("):
            self.assertNotIn(needle, lowered)
        self.assertIn("Then it was live.", body)

    def test_hostile_markup_is_allowlisted_into_well_formed_xhtml(self):
        page = site.analyze("https://e.org/p/", (
            '<body class="single postid-1"><article id="post-1"><div class="entry-content">'
            '<p style="color:red; text-align:center; background:url(javascript:x)">centred</p>'
            '<iframe src="https://evil"></iframe><svg onload="x()"><script>x()</script></svg>'
            '<p>a < b & c > d &nbsp; <img src="data:image/png;base64,AAAA"> <img src="x.png" onerror="x()"></p>'
            '<font color="red"><center>old</center></font><table><tr><td colspan=2 onclick=x>t'
            '</table><a href="https://other.example/x" target="_blank" onmouseover="x()">link</a>'
            '<p>\x01control</p></div></article></body>'))
        [chapter] = site.chapters(page)
        clean.strip_furniture(chapter.content, page.url)
        body = clean.serialize(chapter.content, page.url)
        well_formed(body)
        self.assertIn('<p style="text-align: center">centred</p>', body)
        self.assertIn("a &lt; b &amp; c &gt; d", body)
        self.assertIn('<td colspan="2">t</td>', body)
        self.assertIn('<a href="https://other.example/x">link</a>', body)
        self.assertIn('<img src="images/', body)
        for needle in ("iframe", "svg", "script", "onerror", "onclick", "onmouseover", "target=", "data:", "\x01",
                       "color", "background"):
            self.assertNotIn(needle, body)

    def test_images_resolve_to_downloads_or_disappear(self):
        url_ok, url_bad = "https://e.org/a.png", "https://e.org/b.png"
        body = (f'<p>x<img src="images/{clean.image_token(url_ok)}" alt="a"/>'
                f'<img src="images/{clean.image_token(url_bad)}" alt="b"/></p>')
        resolved = clean.resolve_images(body, {clean.image_token(url_ok): clean.image_token(url_ok) + ".png"})
        self.assertIn(f'src="images/{clean.image_token(url_ok)}.png"', resolved)
        self.assertNotIn(clean.image_token(url_bad), resolved)

    def test_tracking_pixels_and_spacers_are_not_images(self):
        page = site.analyze("https://e.org/p/", (
            '<body class="single postid-1"><article id="post-1"><div class="entry-content"><p>words</p>'
            '<img src="https://pixel.wp.com/g.gif"><img src="/s.gif" width="1" height="1">'
            '<img src="https://i0.wp.com/e.org/real.jpg"></div></article></body>'))
        [chapter] = site.chapters(page)
        clean.strip_furniture(chapter.content, page.url)
        self.assertEqual(["https://i0.wp.com/e.org/real.jpg"], clean.image_urls(chapter.content, page.url))


if __name__ == "__main__":
    unittest.main()
