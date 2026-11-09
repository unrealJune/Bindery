import asyncio
import io
import urllib.error
import unittest
from unittest import mock

from app import royalroad

FFF_403 = "fanficfare.exceptions.HTTPErrorFFF: HTTP Error in FFF '403 Client Error: Forbidden for url: x'(403)"


class BotCheck(unittest.TestCase):
    def test_a_cloudflare_challenge_is_the_site_not_the_login(self):
        error = royalroad.classify(FFF_403)
        self.assertEqual("auth_required", error.code)

        with mock.patch.object(royalroad, "_probe", return_value=True):
            explained = asyncio.run(royalroad.explain(error, "https://www.royalroad.com/fiction/1"))

        self.assertEqual("network", explained.code)
        self.assertTrue(explained.retryable)
        self.assertIn("Cloudflare", explained.message)
        self.assertIn("Calibre", explained.message)
        self.assertIn("upload the EPUB", explained.message)

    def test_a_plain_403_stays_a_refused_login(self):
        error = royalroad.classify(FFF_403)
        with mock.patch.object(royalroad, "_probe", return_value=False):
            self.assertIs(error, asyncio.run(royalroad.explain(error, "https://www.royalroad.com/fiction/1")))

    def test_only_the_marked_interstitial_counts_as_a_challenge(self):
        self.assertTrue(royalroad.is_challenge(403, {"cf-mitigated": "challenge"}))
        self.assertFalse(royalroad.is_challenge(403, {}))
        self.assertFalse(royalroad.is_challenge(200, {"cf-mitigated": "challenge"}))

    def test_a_failed_diagnosis_keeps_the_original_error_and_logs_without_the_url(self):
        error = royalroad.classify(FFF_403)
        with mock.patch.object(royalroad.urllib.request, "urlopen", side_effect=urllib.error.URLError("offline")):
            with self.assertLogs("bindery.hedgerow", level="WARNING") as logs:
                self.assertIs(error, asyncio.run(royalroad.explain(error, "https://www.royalroad.com/fiction/1")))
        self.assertNotIn("/fiction/1", "\n".join(logs.output))

    def test_a_refused_probe_closes_its_response(self):
        body = io.BytesIO(b"challenge")
        refused = urllib.error.HTTPError("https://www.royalroad.com/", 403, "Forbidden",
                                        {"cf-mitigated": "challenge"}, body)
        with mock.patch.object(royalroad.urllib.request, "urlopen", side_effect=refused):
            self.assertTrue(royalroad._probe("https://www.royalroad.com/"))
        self.assertTrue(body.closed)


if __name__ == "__main__":
    unittest.main()
