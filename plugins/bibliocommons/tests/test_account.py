"""Account parsing and sign-in, against a fake browser.

Unlike ``test_catalog.py``, these shapes are **reconstructed**, not captured — no library
credentials were used to build this plugin (see the docstring of ``app/account.py``). What
these tests pin down is the plugin's *behaviour* around those shapes: that a partial account
is never reported as complete, that unknown statuses are reported conservatively, that a
failed sign-in is ``auth_required``, and that secrets never appear in anything returned.
When the real shapes are confirmed, the fixtures here are what should change.
"""

import json
import unittest
from unittest import mock

from app import account, libraries
from app.web import Response, SiteError

SPL = libraries.Library("seattle", "The Seattle Public Library")
LOGIN_PAGE = '<form id="login"><input name="authenticity_token" type="hidden" value="tok123" />' \
             '<input name="user_pin" type="password" /></form>'


class FakeBrowser:
    """Answers requests from a URL-substring table and records what was asked."""

    def __init__(self, routes, cookies=None):
        self.routes = routes
        self.cookies = dict(cookies or {})
        self.calls = []

    def cookie(self, name):
        return self.cookies.get(name)

    def request(self, method, url, **kwargs):
        self.calls.append((method, url, kwargs))
        for fragment, reply in self.routes:
            if fragment in url and (not isinstance(reply, tuple) or reply[0] == method):
                reply = reply[1] if isinstance(reply, tuple) else reply
                if callable(reply):
                    reply = reply(method, url, kwargs)
                if isinstance(reply, Exception):
                    raise reply
                return reply
        raise AssertionError(f"unexpected request {method} {url}")


def page(body, url="https://seattle.bibliocommons.com/user_dashboard"):
    return Response(200, url, body.encode("utf-8"))


def gateway(payload):
    return Response(200, "https://gateway", json.dumps(payload).encode("utf-8"))


BIBS = {
    "S246C1": {"id": "S246C1", "briefInfo": {"title": "Dune", "authors": ["Herbert, Frank"], "format": "BK",
                                             "superFormats": ["BOOKS"]},
               "policy": {"materialType": "PHYSICAL", "provider": "ILS", "holdable": True}, "availability": {}},
    "S246C2": {"id": "S246C2", "briefInfo": {"title": "Dune Messiah", "format": "EBOOK",
                                             "superFormats": ["ELECTRONIC_FORMATS"]},
               "policy": {"materialType": "DIGITAL", "provider": "OverDriveAPI"}, "availability": {}},
}


def holds_payload(entries, pages=1):
    return {"holds": {"results": list(entries), "pagination": {"page": 1, "pages": pages}},
            "entities": {"holds": entries, "bibs": BIBS}}


def session_with(routes):
    browser = FakeBrowser(routes, {"bc_access_token": "tkn", "session_id": "sid"})
    return account.Session(library=SPL, browser=browser, account_id="42", fingerprint="f")


class Snapshot(unittest.TestCase):
    def test_holds_and_loans_map_onto_protocol_states(self):
        session = session_with([
            ("/holds", gateway(holds_payload({
                "h1": {"holdsId": "h1", "metadataId": "S246C1", "status": "READY_FOR_PICKUP",
                       "pickupLocation": {"code": "3", "name": "Central Library"}, "pickupByDate": "2026-11-14"},
                "h2": {"holdsId": "h2", "metadataId": "S246C2", "status": "NOT_YET_AVAILABLE",
                       "holdsPosition": 4, "holdsCount": 31},
            }))),
            ("/checkouts", gateway({"checkouts": {"results": ["c1"], "pagination": {"pages": 1}},
                                    "entities": {"checkouts": {"c1": {"checkoutId": "c1", "metadataId": "S246C1",
                                                                      "dueDate": "2026-11-27", "status": "OUT"}},
                                                 "bibs": BIBS}})),
        ])

        result = account.snapshot(session)
        ready, queued = result["holds"]

        self.assertEqual(("ready", "physical", "Central Library", "2026-11-14"),
                         (ready["state"], ready["medium"], ready["pickupLocation"]["name"], ready["readyUntil"]))
        self.assertEqual(("pending", "digital", 4, 31),
                         (queued["state"], queued["medium"], queued["queuePosition"], queued["queueSize"]))
        self.assertEqual("Dune", ready["title"])
        self.assertEqual("https://seattle.bibliocommons.com/v2/record/S246C1", ready["url"])
        self.assertEqual({"id": "c1", "dueAt": "2026-11-27", "overdue": False, "itemId": "S246C1"},
                         {k: result["loans"][0][k] for k in ("id", "dueAt", "overdue", "itemId")})

    def test_the_session_travels_as_headers_and_the_account_id_as_a_query(self):
        session = session_with([("/holds", gateway(holds_payload({}))),
                                ("/checkouts", gateway({"checkouts": {"results": []}, "entities": {"checkouts": {}}}))])
        account.snapshot(session)

        method, url, kwargs = session.browser.calls[0]
        self.assertEqual("GET", method)
        self.assertIn("accountId=42", url)
        self.assertEqual("tkn", kwargs["headers"]["X-Access-Token"])

    def test_an_unknown_status_is_reported_as_still_queued(self):
        session = session_with([
            ("/holds", gateway(holds_payload({"h1": {"holdsId": "h1", "metadataId": "S246C1", "status": "LEVITATING"}}))),
            ("/checkouts", gateway({"checkouts": {"results": []}, "entities": {"checkouts": {}}})),
        ])
        with self.assertLogs("bindery.bibliocommons", "WARNING"):
            hold = account.snapshot(session)["holds"][0]
        self.assertEqual("pending", hold["state"])
        self.assertEqual("LEVITATING", hold["libraryStatus"])

    def test_a_listed_but_undescribed_hold_fails_the_whole_snapshot(self):
        payload = holds_payload({"h1": {"holdsId": "h1", "metadataId": "S246C1", "status": "IN_TRANSIT"}})
        payload["holds"]["results"].append("h-missing")
        session = session_with([("/holds", gateway(payload))])

        with self.assertRaises(SiteError) as caught:
            account.snapshot(session)
        self.assertEqual("parse", caught.exception.code)

    def test_every_page_is_read(self):
        def paged(method, url, kwargs):
            number = 1 if "page=1&" in url else 2
            return gateway({"holds": {"results": [f"h{number}"], "pagination": {"page": number, "pages": 2}},
                            "entities": {"holds": {f"h{number}": {"holdsId": f"h{number}", "metadataId": "S246C1",
                                                                  "status": "NOT_YET_AVAILABLE"}}, "bibs": BIBS}})
        session = session_with([("/holds", paged),
                                ("/checkouts", gateway({"checkouts": {"results": []}, "entities": {"checkouts": {}}}))])

        self.assertEqual(["h1", "h2"], [h["id"] for h in account.snapshot(session)["holds"]])

    def test_a_failed_page_is_an_error_not_a_shorter_list(self):
        def flaky(method, url, kwargs):
            if "page=1&" in url:
                return gateway({"holds": {"results": ["h1"], "pagination": {"pages": 2}},
                                "entities": {"holds": {"h1": {"holdsId": "h1", "metadataId": "S246C1",
                                                              "status": "NOT_YET_AVAILABLE"}}}})
            return SiteError("network", "page 2 timed out")
        session = session_with([("/holds", flaky)])

        with self.assertRaises(SiteError):
            account.snapshot(session)


class SignIn(unittest.TestCase):
    def test_a_refused_pin_is_auth_required(self):
        browser = FakeBrowser([("/user/login", page(LOGIN_PAGE, "https://seattle.bibliocommons.com/user/login"))])
        with mock.patch.object(account, "Browser", return_value=browser):
            with self.assertRaises(SiteError) as caught:
                account.login(SPL, "1000012345678", "0000", "f")
        self.assertEqual("auth_required", caught.exception.code)
        self.assertNotIn("1000012345678", caught.exception.message)

    def test_a_good_sign_in_posts_the_form_and_finds_the_account(self):
        def post(method, url, kwargs):
            browser.cookies["bc_access_token"] = "tkn"
            return page('<script>window.__STATE__={"accountId":987654}</script>')
        browser = FakeBrowser([("/user/login", ("GET", page(LOGIN_PAGE))), ("/user/login", ("POST", post))])

        with mock.patch.object(account, "Browser", return_value=browser):
            session = account.login(SPL, "1000012345678", "1234", "f")

        form = browser.calls[1][2]["form"]
        self.assertEqual(("tok123", "1000012345678", "1234"), (form["authenticity_token"], form["name"], form["user_pin"]))
        self.assertEqual("987654", session.account_id)

    def test_a_changed_pin_drops_the_cached_session(self):
        sessions = account.Sessions()
        made = []

        def fake_login(library, card, pin, fingerprint):
            made.append(pin)
            return account.Session(library=library, browser=None, account_id="1", fingerprint=fingerprint)

        with mock.patch.object(account, "login", side_effect=fake_login):
            sessions.get("acct", SPL, "1000", "1111")
            sessions.get("acct", SPL, "1000", "1111")
            sessions.get("acct", SPL, "1000", "2222")

        self.assertEqual(["1111", "2222"], made)


class Holds(unittest.TestCase):
    def test_a_refusal_becomes_a_circulation_error_code(self):
        refusal = Response(409, "https://gateway", b'{"error":{"message":"You already have a hold on this title"}}')
        session = session_with([("/holds", refusal)])

        with self.assertRaises(SiteError) as caught:
            account.place_hold(session, "S246C1", "3")
        self.assertEqual("already_held", caught.exception.code)

        body = session.browser.calls[0][2]["json_body"]
        self.assertEqual({"metadataId": "S246C1", "materialType": "PHYSICAL", "pickupLocation": "3", "accountId": 42},
                         {k: body[k] for k in ("metadataId", "materialType", "pickupLocation", "accountId")})


if __name__ == "__main__":
    unittest.main()
