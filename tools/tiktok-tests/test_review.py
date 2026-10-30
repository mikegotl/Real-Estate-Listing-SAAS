import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("review", Path(__file__).with_name("review.py"))
review = importlib.util.module_from_spec(spec)
spec.loader.exec_module(review)


class ReviewTests(unittest.TestCase):
    def probe(self):
        return {"streams": [
            {"codec_type": "video", "width": 1080, "height": 1920, "codec_name": "h264", "r_frame_rate": "30/1"},
            {"codec_type": "audio", "codec_name": "aac"}], "format": {"duration": "30.02"}}

    def test_valid_vertical_output(self):
        self.assertEqual([], review.media_findings(self.probe(), 30))

    def test_wrong_orientation_codec_and_duration_fail(self):
        probe = self.probe()
        probe["streams"][0].update(width=1920, height=1080, codec_name="hevc")
        probe["format"]["duration"] = "60"
        self.assertEqual(4, len(review.media_findings(probe, 30)))

    def test_missing_audio_and_zero_frame_rate_fail(self):
        probe = self.probe()
        probe["streams"] = probe["streams"][:1]
        probe["streams"][0]["r_frame_rate"] = "0/0"
        self.assertEqual(2, len(review.media_findings(probe, 30)))

    def test_unrun_cases_cannot_look_passed_and_content_is_escaped(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            case = root / "short-voiceover"
            case.mkdir()
            (case / "result.json").write_text(json.dumps({"case_id": "short-voiceover", "technical_status": "failed", "error": "<script>alert(1)</script>"}))
            records = review.gallery(root)
            self.assertEqual(["failed", "not run", "not run"], [r["technical_status"] for r in records])
            page = (root / "index.html").read_text()
            self.assertNotIn("<script>", page)
            self.assertIn("&lt;script&gt;", page)


if __name__ == "__main__":
    unittest.main()
