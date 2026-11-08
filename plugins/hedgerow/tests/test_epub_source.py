"""What an EPUB is allowed to claim about its own identity.

`epub.read_source_url` decides whether a forwarded advance copy can be associated with a
work nobody has bound by hand. Getting it wrong is quiet in both directions — too strict and
files are silently ignored, too loose and two unrelated serials merge into one book — so the
cases are pinned here. Stdlib only, like the rest of this suite. Run from
`plugins/hedgerow`:

    python -m unittest discover -s tests -t .
"""

from __future__ import annotations

import os
import sys
import tempfile
import unittest
import zipfile

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

os.environ.setdefault("BINDERY_PLUGIN_WORKDIR", tempfile.mkdtemp(prefix="hedgerow-tests-"))

from app import epub  # noqa: E402

RR = "https://www.royalroad.com/fiction/140087"


def epub_with(metadata: str) -> str:
    """A minimal but structurally valid EPUB carrying the given <metadata> children."""
    handle = tempfile.NamedTemporaryFile(suffix=".epub", delete=False)
    handle.close()
    with zipfile.ZipFile(handle.name, "w") as book:
        book.writestr(
            "META-INF/container.xml",
            '<container><rootfiles><rootfile full-path="OEBPS/content.opf"/></rootfiles></container>',
        )
        book.writestr(
            "OEBPS/content.opf",
            '<package xmlns:dc="http://purl.org/dc/elements/1.1/">'
            f"<metadata>{metadata}</metadata><manifest/><spine/></package>",
        )
    return handle.name


class ReadSourceUrl(unittest.TestCase):
    def test_dc_source_is_read(self):
        self.assertEqual(epub.read_source_url(epub_with(f"<dc:source>{RR}</dc:source>")), RR)

    def test_identifier_is_used_when_it_is_a_url(self):
        self.assertEqual(epub.read_source_url(epub_with(f"<dc:identifier>{RR}</dc:identifier>")), RR)

    def test_source_wins_over_a_misleading_identifier(self):
        """dc:source means 'where this came from'; dc:identifier is a URL only by accident."""
        found = epub.read_source_url(epub_with(
            '<dc:identifier>https://example.com/wrong</dc:identifier>'
            f"<dc:source>{RR}</dc:source>"))
        self.assertEqual(found, RR)

    def test_non_url_identifier_is_not_a_source(self):
        self.assertIsNone(epub.read_source_url(epub_with("<dc:identifier>urn:uuid:1234</dc:identifier>")))

    def test_a_title_is_never_an_identity(self):
        """Two serials can share a title; merging on one would be silent and unrecoverable."""
        self.assertIsNone(epub.read_source_url(epub_with("<dc:title>Maidens of the Fall</dc:title>")))

    def test_unreadable_file_is_none_not_an_exception(self):
        """Ingest runs inside a Discord handler; a malformed upload must not raise there."""
        handle = tempfile.NamedTemporaryFile(suffix=".epub", delete=False)
        handle.write(b"this is not a zip")
        handle.close()
        self.assertIsNone(epub.read_source_url(handle.name))

    def test_missing_file_is_none(self):
        self.assertIsNone(epub.read_source_url("/nonexistent/nope.epub"))


if __name__ == "__main__":
    unittest.main()
