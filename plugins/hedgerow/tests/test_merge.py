"""Chapter-number parsing and the ordering it feeds.

Pure stdlib `unittest` and no fixtures on purpose: `merge` is the one piece of Hedgerow
that has to agree with itself across two ingest paths and a UI, and it should stay testable
without FastAPI, a database, or a container. Run from `plugins/hedgerow`:

    python -m unittest discover -s tests -t .
"""

from __future__ import annotations

import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

# `store` opens a SQLite file at import-driven default paths, so point the work directory at
# a throwaway before anything under `app` is imported.
os.environ.setdefault("BINDERY_PLUGIN_WORKDIR", tempfile.mkdtemp(prefix="hedgerow-tests-"))

from app import merge  # noqa: E402
from app.store import Chapter, Store  # noqa: E402


def chapter(**kwargs) -> Chapter:
    defaults = dict(
        id=1, work_id=1, chapter_key="k", source="royalroad", title="",
        arc=None, part=None, published=None, discord_ts=None,
        pinned_number=None, manual_order=None, first_seen=0.0,
    )
    defaults.update(kwargs)
    return Chapter(**defaults)


class ParseNumber(unittest.TestCase):
    def test_a_labelled_arc_supplies_the_major_number(self):
        # The case this parser was rewritten for: the chapter marker is not the last number.
        self.assertEqual(
            (6, 3),
            merge.parse_number("Maidens of the Fall, Arc 6, Zodiacal Light, Chapter 3"),
        )

    def test_the_arc_wins_wherever_it_sits_in_the_title(self):
        for title in (
            "Arc 6, Chapter 3",
            "Chapter 3 (Arc 6)",
            "Zodiacal Light - Chapter 3 - Arc 6",
            "Book 6: Chapter 3",
        ):
            with self.subTest(title=title):
                self.assertEqual((6, 3), merge.parse_number(title))

    def test_separators_between_a_label_and_its_number_do_not_matter(self):
        for title in ("Chapter 7", "Chapter: 7", "Ch. 7", "Ch 7", "Ep #7", "Chapter—7"):
            with self.subTest(title=title):
                self.assertEqual((7, 0), merge.parse_number(title))

    def test_a_decimal_is_arc_dot_part_with_or_without_a_label(self):
        self.assertEqual((6, 1), merge.parse_number("Zodiacal Light – 6.1"))
        self.assertEqual((6, 3), merge.parse_number("Chapter 6.3"))
        self.assertEqual((6, 3), merge.parse_number("Arc 6.3"))

    def test_two_minor_labels_read_as_major_then_minor(self):
        self.assertEqual((2, 5), merge.parse_number("Part 2, Chapter 5"))

    def test_roman_arcs_are_read_and_impostors_are_not(self):
        self.assertEqual((6, 3), merge.parse_number("Arc VI, Chapter 3"))
        self.assertEqual((4, 0), merge.parse_number("Book IV"))
        # 'Ill' is not a roman numeral, however much it looks like one.
        self.assertEqual((None, None), merge.parse_number("Book Ill"))

    def test_a_label_inside_another_word_is_not_a_label(self):
        # 'March' ends in 'ch'; 'Arcane' starts with 'arc'. Neither is a chapter marker.
        self.assertEqual((5, 0), merge.parse_number("The March 5"))
        self.assertEqual((2, 0), merge.parse_number("Arcane Tides 2"))

    def test_an_unlabelled_title_falls_back_to_its_last_number(self):
        self.assertEqual((7, 0), merge.parse_number("7"))
        self.assertEqual((12, 0), merge.parse_number("Zodiacal Light 12"))

    def test_a_title_with_no_number_has_no_number(self):
        for title in ("Interlude", "", None, "Epilogue: The Long Dark"):
            with self.subTest(title=title):
                self.assertEqual((None, None), merge.parse_number(title))


class ChapterKey(unittest.TestCase):
    def test_the_two_sources_agree_on_a_key_even_when_they_disagree_on_wording(self):
        # This is the whole of first-seen dedup: Royal Road spells it out, the forwarded
        # EPUB numbers it, and both must land on the same key or the chapter arrives twice.
        self.assertEqual(
            merge.chapter_key("Maidens of the Fall, Arc 6, Zodiacal Light, Chapter 3"),
            merge.chapter_key("6.3"),
        )

    def test_an_unnumbered_chapter_keys_on_its_normalized_title(self):
        self.assertEqual(merge.chapter_key("  Interlude:  Rain "), merge.chapter_key("interlude: rain"))
        self.assertTrue(merge.chapter_key("Interlude").startswith("t:"))

    def test_different_chapters_do_not_collide(self):
        self.assertNotEqual(merge.chapter_key("Arc 6, Chapter 3"), merge.chapter_key("Arc 6, Chapter 4"))
        self.assertNotEqual(merge.chapter_key("Arc 6, Chapter 3"), merge.chapter_key("Arc 7, Chapter 3"))


class Ordering(unittest.TestCase):
    def test_parsed_numbers_sort_numerically_not_lexically(self):
        chapters = [
            chapter(id=1, arc=6, part=10),
            chapter(id=2, arc=6, part=2),
            chapter(id=3, arc=5, part=9),
        ]
        self.assertEqual([3, 2, 1], [c.id for c in merge.order_chapters(chapters)])

    def test_manual_order_beats_a_pin_and_a_pin_beats_a_parsed_number(self):
        chapters = [
            chapter(id=1, arc=1, part=0),
            chapter(id=2, arc=99, part=0, pinned_number=0.5),
            chapter(id=3, arc=99, part=0, manual_order=0),
        ]
        self.assertEqual([3, 2, 1], [c.id for c in merge.order_chapters(chapters)])

    def test_an_unnumbered_chapter_falls_back_to_its_dates(self):
        chapters = [
            chapter(id=1, discord_ts=200.0),
            chapter(id=2, published="2024-01-01T00:00:00Z"),
            chapter(id=3, discord_ts=100.0),
        ]
        # Publish date outranks a Discord timestamp; timestamps then sort among themselves.
        self.assertEqual([2, 3, 1], [c.id for c in merge.order_chapters(chapters)])


class Reindex(unittest.TestCase):
    """The migration that stops a parser improvement re-ingesting the back catalogue."""

    def setUp(self):
        self.dir = tempfile.mkdtemp(prefix="hedgerow-store-")
        self.store = Store(os.path.join(self.dir, "test.db"))
        self.work = self.store.get_or_create_work(source_url="https://example.test/fiction/1")

    def test_stored_keys_are_rewritten_once_per_parser_version(self):
        title = "Maidens of the Fall, Arc 6, Zodiacal Light, Chapter 3"

        # As the previous parser would have stored it: the last number in the title.
        self.store.ingest_chapter(
            work_id=self.work.id, chapter_key="n:3.0", source="royalroad",
            title=title, body_html="<p>x</p>", arc=3, part=0,
        )

        rewritten = self.store.reindex_chapters(
            merge.chapter_key, merge.parse_number, version=merge.KEY_VERSION)
        self.assertEqual(1, rewritten)

        stored = self.store.list_chapters(self.work.id)
        self.assertEqual(1, len(stored))
        self.assertEqual("n:6.3", stored[0].chapter_key)
        self.assertEqual((6, 3), (stored[0].arc, stored[0].part))

        # Idempotent: the guard setting means a restart does not walk the table again.
        self.assertEqual(
            0,
            self.store.reindex_chapters(merge.chapter_key, merge.parse_number,
                                        version=merge.KEY_VERSION))

    def test_a_reingest_after_reindexing_is_correctly_recognised_as_a_duplicate(self):
        self.store.ingest_chapter(
            work_id=self.work.id, chapter_key="n:3.0", source="royalroad",
            title="Arc 6, Chapter 3", body_html="<p>x</p>", arc=3, part=0,
        )
        self.store.reindex_chapters(merge.chapter_key, merge.parse_number,
                                    version=merge.KEY_VERSION)

        inserted = self.store.ingest_chapter(
            work_id=self.work.id, chapter_key=merge.chapter_key("6.3"), source="discord",
            title="6.3", body_html="<p>y</p>", arc=6, part=3,
        )

        self.assertFalse(inserted)
        self.assertEqual(1, len(self.store.list_chapters(self.work.id)))

    def test_a_new_key_already_held_by_another_row_does_not_collide(self):
        """The rewrite must not trip UNIQUE(work_id, chapter_key) on its way through.

        Chapter 6.3 is stored under the old parser's answer ('n:3.0'), and a *different*
        chapter happens to sit on 'n:6.3' already. Rewriting the first in place would
        collide with the second before the second has given the key up.
        """
        self.store.ingest_chapter(
            work_id=self.work.id, chapter_key="n:3.0", source="royalroad",
            title="Arc 6, Chapter 3", body_html="<p>six three</p>", arc=3, part=0,
        )
        self.store.ingest_chapter(
            work_id=self.work.id, chapter_key="n:6.3", source="royalroad",
            title="Chapter 6.3 Reprise", body_html="<p>squatter</p>", arc=6, part=3,
        )

        self.store.reindex_chapters(merge.chapter_key, merge.parse_number,
                                    version=merge.KEY_VERSION)

        stored = self.store.list_chapters(self.work.id)
        # Both titles parse to 6.3, so the later one collapses onto the earlier.
        self.assertEqual(1, len(stored))
        self.assertEqual("n:6.3", stored[0].chapter_key)
        self.assertIn("six three", self.store.chapter_body(stored[0].id))

    def test_distinct_chapters_survive_a_key_that_has_to_move_past_a_neighbour(self):
        # 6.3 is stored where 6.4 will land, and 6.4 is stored where 6.5 will land: every
        # rewrite steps onto a key still held by the row after it.
        for old_key, title in (("n:6.4", "Arc 6, Chapter 3"),
                               ("n:6.5", "Arc 6, Chapter 4"),
                               ("n:6.6", "Arc 6, Chapter 5")):
            self.store.ingest_chapter(
                work_id=self.work.id, chapter_key=old_key, source="royalroad",
                title=title, body_html="<p>x</p>",
            )

        self.store.reindex_chapters(merge.chapter_key, merge.parse_number,
                                    version=merge.KEY_VERSION)

        stored = sorted(self.store.list_chapters(self.work.id), key=lambda c: c.chapter_key)
        self.assertEqual(["n:6.3", "n:6.4", "n:6.5"], [c.chapter_key for c in stored])

    def test_rows_that_collapse_onto_one_key_keep_the_earliest(self):
        # Two spellings the old parser told apart and the new one does not.
        self.store.ingest_chapter(
            work_id=self.work.id, chapter_key="a", source="royalroad",
            title="Arc 6, Chapter 3", body_html="<p>first</p>",
        )
        self.store.ingest_chapter(
            work_id=self.work.id, chapter_key="b", source="discord",
            title="6.3", body_html="<p>second</p>",
        )

        self.store.reindex_chapters(merge.chapter_key, merge.parse_number,
                                    version=merge.KEY_VERSION)

        stored = self.store.list_chapters(self.work.id)
        self.assertEqual(1, len(stored))
        self.assertEqual("royalroad", stored[0].source)
        self.assertIn("first", self.store.chapter_body(stored[0].id))


class Deletion(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp(prefix="hedgerow-store-")
        self.store = Store(os.path.join(self.dir, "test.db"))

    def test_forgetting_a_work_takes_its_chapters_and_bindings_with_it(self):
        work = self.store.get_or_create_work(source_url="https://example.test/fiction/1")
        other = self.store.get_or_create_work(source_url="https://example.test/fiction/2")
        self.store.bind_channel("111", work.id)
        self.store.bind_channel("222", other.id)
        self.store.ingest_chapter(
            work_id=work.id, chapter_key="n:1.0", source="royalroad",
            title="Chapter 1", body_html="<p>x</p>",
        )

        self.assertTrue(self.store.delete_work(work.id))

        self.assertIsNone(self.store.get_work(work.id))
        self.assertEqual([], self.store.list_chapters(work.id))
        self.assertEqual(["222"], [b["channel_id"] for b in self.store.bindings()])
        # Only the named work goes.
        self.assertIsNotNone(self.store.get_work(other.id))

    def test_forgetting_a_work_that_is_not_there_says_so(self):
        self.assertFalse(self.store.delete_work(9999))

    def test_unbinding_a_channel_leaves_the_work_and_its_chapters(self):
        work = self.store.get_or_create_work(source_url="https://example.test/fiction/1")
        self.store.bind_channel("111", work.id)
        self.store.ingest_chapter(
            work_id=work.id, chapter_key="n:1.0", source="discord",
            title="Chapter 1", body_html="<p>x</p>",
        )

        self.assertTrue(self.store.unbind_channel("111"))

        self.assertEqual([], self.store.bindings())
        self.assertIsNone(self.store.work_for_channel("111"))
        self.assertEqual(1, len(self.store.list_chapters(work.id)))
        self.assertFalse(self.store.unbind_channel("111"))

    def test_deleting_one_chapter_frees_its_key_for_the_next_source(self):
        work = self.store.get_or_create_work(source_url="https://example.test/fiction/1")
        self.store.ingest_chapter(
            work_id=work.id, chapter_key="n:6.3", source="royalroad",
            title="Arc 6, Chapter 3", body_html="<p>bad</p>",
        )
        stored = self.store.list_chapters(work.id)

        self.assertTrue(self.store.delete_chapter(work.id, stored[0].id))
        self.assertEqual([], self.store.list_chapters(work.id))

        # First-seen precedence starts over: whoever supplies it next owns it.
        self.assertTrue(self.store.ingest_chapter(
            work_id=work.id, chapter_key="n:6.3", source="discord",
            title="6.3", body_html="<p>good</p>",
        ))

    def test_a_chapter_cannot_be_deleted_through_the_wrong_work(self):
        work = self.store.get_or_create_work(source_url="https://example.test/fiction/1")
        other = self.store.get_or_create_work(source_url="https://example.test/fiction/2")
        self.store.ingest_chapter(
            work_id=work.id, chapter_key="n:1.0", source="royalroad",
            title="Chapter 1", body_html="<p>x</p>",
        )
        stored = self.store.list_chapters(work.id)

        self.assertFalse(self.store.delete_chapter(other.id, stored[0].id))
        self.assertEqual(1, len(self.store.list_chapters(work.id)))


if __name__ == "__main__":
    unittest.main()
