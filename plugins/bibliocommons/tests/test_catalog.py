"""Catalog mapping against responses captured from the live SPL and KCLS gateways.

The fixtures are real responses (descriptions trimmed for size), captured November 2026:
``search-seattle-dune.json`` is SPL's ``/bibs/search?query=dune``, ``search-kcls-online.json``
is KCLS's same search with ``f_STATUS=_online_``, and ``branches-seattle.json`` is the STATUS
facet of a broad physical search at SPL.
"""

import json
import os
import unittest

from app import catalog, libraries

FIXTURES = os.path.join(os.path.dirname(__file__), "fixtures")
SPL = libraries.Library("seattle", "The Seattle Public Library")
KCLS = libraries.Library("kcls", "King County Library System")


def load(name):
    with open(os.path.join(FIXTURES, name), encoding="utf-8") as handle:
        return json.load(handle)


def by_format(items):
    return {item["formatLabel"]: item for item in items}


class PhysicalOrDigital(unittest.TestCase):
    def test_spl_search_separates_shelf_copies_from_licences(self):
        items = by_format(catalog.items_from_search(SPL, load("search-seattle-dune.json"), "any"))

        for label in ("Book", "DVD", "Graphic novel", "Audiobook CD"):
            self.assertEqual("physical", items[label]["medium"], label)
            self.assertIsNone(items[label]["provider"])
            self.assertIsNone(items[label]["borrowUrl"])

        ebook = items["eBook"]
        self.assertEqual("digital", ebook["medium"])
        self.assertEqual("OverDrive (Libby)", ebook["provider"])
        self.assertFalse(ebook["holdable"], "digital holds belong to the lending platform, not the ILS")
        self.assertTrue(ebook["borrowUrl"].startswith("https://seattle.bibliocommons.com/v2/record/"))

    def test_a_physical_record_flagged_electronic_is_digital(self):
        # KCLS lists hoopla music as materialType PHYSICAL, provider ILS — with an e-resource
        # link and the ELECTRONIC_FORMATS super-format. Nobody should drive to a branch for it.
        items = by_format(catalog.items_from_search(KCLS, load("search-kcls-online.json"), "any"))
        music = next(item for label, item in items.items() if "Music" in label)

        self.assertEqual("digital", music["medium"])
        self.assertEqual("hoopla", music["provider"])
        self.assertTrue(music["borrowUrl"].startswith("https://www.hoopladigital.com/"))
        self.assertEqual("digital", items["eAudiobook"]["medium"])
        self.assertEqual("audiobook", items["eAudiobook"]["format"])

    def test_the_medium_filter_is_enforced_here_not_trusted_to_the_facet(self):
        response = load("search-kcls-online.json")
        self.assertTrue(all(i["medium"] == "digital" for i in catalog.items_from_search(KCLS, response, "digital")))
        self.assertEqual([], catalog.items_from_search(KCLS, response, "physical"))

        spl = load("search-seattle-dune.json")
        physical = catalog.items_from_search(SPL, spl, "physical")
        self.assertEqual(4, len(physical))
        self.assertTrue(all(i["medium"] == "physical" for i in physical))

    def test_results_keep_the_library_ranking(self):
        response = load("search-seattle-dune.json")
        expected = [r["representative"] for r in response["catalogSearch"]["results"]]
        self.assertEqual(expected, [i["id"] for i in catalog.items_from_search(SPL, response, "any")])


class Availability(unittest.TestCase):
    def test_counts_and_status_come_through(self):
        items = by_format(catalog.items_from_search(SPL, load("search-seattle-dune.json"), "any"))
        book = items["Book"]

        self.assertEqual("unavailable", book["availability"]["status"])
        self.assertEqual(24, book["availability"]["holds"])
        self.assertEqual(12, book["availability"]["copies"])
        self.assertTrue(book["holdable"])
        self.assertEqual("available", items["DVD"]["availability"]["status"])

    def test_a_non_circulating_copy_is_not_holdable(self):
        cd = by_format(catalog.items_from_search(SPL, load("search-seattle-dune.json"), "any"))["Audiobook CD"]
        self.assertEqual("not_holdable", cd["availability"]["status"])
        self.assertFalse(cd["holdable"])

    def test_covers_are_https_only(self):
        for item in catalog.items_from_search(SPL, load("search-seattle-dune.json"), "any"):
            self.assertTrue(item["coverUrl"] is None or item["coverUrl"].startswith("https://"))


class Branches(unittest.TestCase):
    def test_pickup_branches_exclude_places_nobody_collects_from(self):
        names = [b["name"] for b in catalog.pickup_locations(load("branches-seattle.json"))]

        self.assertIn("Central Library", names)
        self.assertIn("Ballard Branch", names)
        for not_a_branch in ("Digital Resources", "Mobile Services", "Technical and Collection Services"):
            self.assertNotIn(not_a_branch, names)
        self.assertEqual(sorted(names, key=str.lower), names)
        self.assertGreaterEqual(len(names), 24)

    def test_branch_ids_are_the_library_codes(self):
        central = next(b for b in catalog.pickup_locations(load("branches-seattle.json")) if b["name"] == "Central Library")
        self.assertEqual("3", central["id"])


class Libraries(unittest.TestCase):
    def test_presets_cover_spl_and_kcls(self):
        self.assertEqual("seattle", libraries.resolve({"library": "seattle"}).slug)
        self.assertEqual("King County Library System", libraries.resolve({"library": "kcls"}).name)

    def test_any_bibliocommons_library_by_subdomain(self):
        self.assertEqual("nypl", libraries.resolve({"library": "other", "library_other": "NYPL"}).slug)
        self.assertEqual("nypl", libraries.resolve({"library": "other", "library_other": "https://nypl.bibliocommons.com/"}).slug)
        self.assertEqual("https://gateway.bibliocommons.com/v2/libraries/nypl",
                         libraries.resolve({"library": "other", "library_other": "nypl"}).gateway)

    def test_a_slug_cannot_steer_requests_to_another_host(self):
        for hostile in ("evil.com/", "a@evil.com", "x.evil.com#", "../etc", "nypl.bibliocommons.com.evil.com", "", "other"):
            with self.assertRaises(libraries.UnknownLibrary, msg=hostile):
                libraries.resolve({"library": "other", "library_other": hostile})

    def test_operators_can_add_libraries_without_code(self):
        os.environ["BIBLIOCOMMONS_LIBRARIES"] = "sfpl=San Francisco Public Library, bad slug!=Nope"
        try:
            values = [o["value"] for o in libraries.options()]
            self.assertIn("sfpl", values)
            self.assertNotIn("bad slug!", values)
            self.assertEqual("other", values[-1])
            self.assertEqual("San Francisco Public Library", libraries.resolve({"library": "sfpl"}).name)
        finally:
            del os.environ["BIBLIOCOMMONS_LIBRARIES"]

    def test_the_library_name_is_learned_from_the_gateway(self):
        bare = libraries.Library("seattle", "seattle")
        named = libraries.name_from_sites(bare, load("search-seattle-dune.json")["entities"]["sites"])
        self.assertEqual("The Seattle Public Library", named.name)


class GatewayErrors(unittest.TestCase):
    def test_an_unavailable_library_is_not_reported_as_a_bad_pin(self):
        from app.web import Response, classify
        # Captured: the gateway's answer for nypl, which is not a public BiblioCommons catalog.
        reply = Response(403, "https://gateway", b'{"error":{"code":"not_available","message":"This library is not available yet."}}')
        self.assertEqual("not_found", classify(reply).code)
        self.assertEqual("auth_required", classify(Response(401, "https://gateway", b'{"error":{"classification":"invalid_token"}}')).code)


if __name__ == "__main__":
    unittest.main()
