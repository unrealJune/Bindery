"""The admin/member split (PLUGIN-PROTOCOL.md §2.3, §5, §6).

Bindery refuses `ui.adminPaths` and `access: admin` actions to members before forwarding;
`main.py` checks the same list again. These pin the list itself: that it covers every
route that changes the shared store and none of the ones a member reads.
"""

import unittest

from app.manifest import ADMIN_ACTIONS, build_manifest, is_admin_path


class AdminSplitTests(unittest.TestCase):
    def test_every_mutating_route_is_an_admin_path(self):
        for route in ("sources", "/sources/", "api/order", "api/pin", "api/reset",
                      "api/work/delete", "api/binding/delete", "api/chapter/delete"):
            with self.subTest(route=route):
                self.assertTrue(is_admin_path(route))

    def test_what_a_member_reads_is_not(self):
        for route in ("", "/", "api/state", "api/chapters", "app.js", "style.css", "sourcesx"):
            with self.subTest(route=route):
                self.assertFalse(is_admin_path(route))

    def test_the_manifest_declares_what_main_enforces(self):
        manifest = build_manifest()
        ui = manifest["ui"]

        self.assertTrue(all(is_admin_path(prefix) for prefix in ui["adminPaths"]))
        sources = next(entry for entry in ui["nav"] if entry["path"] == "/sources")
        self.assertEqual("admin", sources.get("access"))

        declared = {action["name"] for action in manifest["actions"] if action.get("access") == "admin"}
        self.assertEqual(ADMIN_ACTIONS, declared)


if __name__ == "__main__":
    unittest.main()
