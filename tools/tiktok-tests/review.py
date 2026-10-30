"""Review retained renderer output, or run explicitly authorized browser campaigns.

The default browser mode reads one existing output without changing campaign settings.
Playwright is imported only by the browser command; gallery generation needs Python alone.
"""
import argparse
import asyncio
import html
import json
import os
from pathlib import Path
import subprocess
import time
from urllib.parse import urlparse


CASES = (
    ("short-voiceover", "Short voiceover (30 seconds)", "Fast cuts on the beat", 30),
    ("music-text-only", "Music and text only (30 seconds)", "Fast cuts on the beat", 30),
    ("full-voiceover", "Full tour voiceover (60 seconds)", "Cinematic glide", 60),
)


def media_findings(probe, expected_seconds=None):
    streams = probe.get("streams", [])
    videos = [s for s in streams if s.get("codec_type") == "video"]
    audio = [s for s in streams if s.get("codec_type") == "audio"]
    failures = []
    if len(videos) != 1:
        failures.append("Expected one video stream")
    else:
        v = videos[0]
        for key, value in (("width", 1080), ("height", 1920), ("codec_name", "h264")):
            if v.get(key) != value:
                failures.append(f"Expected video {key}={value}; found {v.get(key)}")
        numerator, denominator = v.get("r_frame_rate", "0/1").split("/")
        if not float(denominator) or abs(float(numerator) / float(denominator) - 30) > 0.01:
            failures.append("Expected 30 fps")
    if len(audio) != 1 or audio[0].get("codec_name") != "aac":
        failures.append("Expected one AAC audio stream")
    duration = float(probe.get("format", {}).get("duration", 0))
    if duration <= 0:
        failures.append("Missing positive duration")
    if expected_seconds is not None and abs(duration - expected_seconds) > 0.2:
        failures.append(f"Expected {expected_seconds}s; found {duration}s")
    return failures


def inspect_video(directory, expected_seconds=None):
    video = directory / "video.mp4"
    result = subprocess.run(
        [os.environ.get("FFPROBE_PATH", "ffprobe"), "-v", "error", "-show_streams", "-show_format", "-of", "json", str(video)],
        capture_output=True, text=True, check=True, timeout=60)
    probe = json.loads(result.stdout)
    (directory / "probe.json").write_text(json.dumps(probe, indent=2))
    failures = media_findings(probe, expected_seconds)
    ffmpeg = os.environ.get("FFMPEG_PATH", "ffmpeg")
    # Decode the complete file; a correct container header alone is insufficient.
    subprocess.run([ffmpeg, "-v", "error", "-xerror", "-i", str(video), "-f", "null", "-"],
                   capture_output=True, check=True, timeout=300)
    duration = float(probe["format"]["duration"])
    subprocess.run([ffmpeg, "-v", "error", "-y", "-i", str(video), "-vf",
                    f"fps=1/{max(1, duration / 6)},scale=180:320,tile=6x1", "-frames:v", "1",
                    str(directory / "contact-sheet.jpg")], capture_output=True, check=True, timeout=120)
    return failures


def gallery(root):
    root.mkdir(parents=True, exist_ok=True)
    cards = []
    records = []
    for case_id, _, _, _ in CASES:
        path = root / case_id / "result.json"
        record = json.loads(path.read_text()) if path.exists() else {
            "case_id": case_id, "technical_status": "not run", "editorial_status": "not reviewed"}
        records.append(record)
    existing = root / "existing" / "result.json"
    if existing.exists():
        records.append(json.loads(existing.read_text()))
    for record in records:
        case_id = record["case_id"]
        # IDs come from our fixed matrix, never from property or provider content.
        if case_id not in {c[0] for c in CASES} | {"existing"}:
            raise ValueError("Unknown case ID")
        video = root / case_id / "video.mp4"
        player = f'<video controls preload="metadata" src="{case_id}/video.mp4"></video>' if video.exists() else "<p>No video produced.</p>"
        sheet = f'<a href="{case_id}/contact-sheet.jpg">View sampled frames</a>' if (root / case_id / "contact-sheet.jpg").exists() else ""
        cards.append(f'<article><h2>{html.escape(case_id)}</h2>{player}{sheet}<pre>{html.escape(json.dumps(record, indent=2))}</pre></article>')
    page = '''<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>TikTok campaign review</title><style>body{font:16px system-ui;margin:32px;background:#102131;color:#eef4fa}
main{display:grid;grid-template-columns:repeat(auto-fit,minmax(300px,1fr));gap:24px}article{background:#1c3448;padding:20px;border-radius:16px}
video{width:100%;max-height:620px;background:#000}pre{white-space:pre-wrap;overflow-wrap:anywhere;font-size:13px}a{color:#a9d9ff}</style>
<h1>TikTok campaign review</h1><p>Technical checks and editorial approval are separate. Test tones are not a review of spoken narration.
Watch the entire video, check facts against the listing, and review captions, framing, pacing, sound and closing card.</p><main>'''
    (root / "index.html").write_text(page + "".join(cards) + "</main></html>")
    return records


async def choose(page, label, value):
    await page.get_by_role("combobox", name=label, exact=True).click()
    await page.get_by_role("option", name=value, exact=True).click()


async def browser_run(args):
    from playwright.async_api import async_playwright
    parsed = urlparse(args.property_url)
    if parsed.scheme not in {"http", "https"} or not parsed.hostname:
        raise ValueError("Use a complete property URL")
    if args.generate and args.max_new_campaigns not in range(1, 4):
        raise ValueError("Generation requires --max-new-campaigns 1, 2 or 3; this is a render-count limit, not a dollar cap")
    root = Path(args.output).resolve()
    if root.exists() and any(root.iterdir()):
        raise ValueError("Use a fresh output directory so results cannot mix between runs")
    root.mkdir(parents=True, exist_ok=True)
    results = []
    async with async_playwright() as p:
        browser = await p.chromium.launch(headless=not args.headed)
        context = await browser.new_context(storage_state=args.storage_state, viewport={"width": 1440, "height": 1000})
        page = await context.new_page()
        page.set_default_timeout(30000)
        cases = CASES[:args.max_new_campaigns] if args.generate else (("existing", None, None, None),)
        for case_id, voiceover, pace, seconds in cases:
            directory = root / case_id
            directory.mkdir()
            record = {"case_id": case_id, "scope": "live browser workflow" if args.generate else "existing output audit; no generation",
                      "technical_status": "failed", "editorial_status": "needs human review",
                      "voiceover": voiceover, "pace": pace, "expected_seconds": seconds}
            try:
                await page.goto(args.property_url, wait_until="domcontentloaded")
                await page.get_by_role("tab", name="Overview", exact=True).click()
                await page.screenshot(path=str(directory / "overview.png"), full_page=True)
                await page.get_by_role("tab", name="Story & script", exact=True).click()
                await page.screenshot(path=str(directory / "story.png"), full_page=True)
                await page.get_by_role("tab", name="Campaign", exact=True).click()
                if args.generate:
                    # Keep each case to one supported vertical output. Never grant portrait consent.
                    await page.get_by_role("checkbox", name="Vertical video for TikTok, Reels and Shorts", exact=True).check()
                    for label in ("YouTube property tour", "Facebook Feed", "Facebook still-image post", "Full narrated tour"):
                        box = page.get_by_role("checkbox", name=label, exact=True)
                        if await box.is_enabled():
                            await box.uncheck()
                    await choose(page, "Marketing template", "Balanced showcase")
                    await choose(page, "Voiceover", voiceover)
                    await choose(page, "Style", pace)
                    await choose(page, "Music", "Modern ambient")
                    # Expand the advanced panel and disable paid presenter/motion for this initial matrix.
                    await page.get_by_text("Advanced: on-screen elements and captions", exact=True).click()
                    for label in ("AI agent presenter", "Agent photo on closing card", "Use walkthrough video and AI walk-through clips"):
                        await page.get_by_role("checkbox", name=label, exact=True).uncheck()
                    await page.screenshot(path=str(directory / "settings.png"), full_page=True)
                    old = await page.get_by_role("link", name="Download MP4", exact=True).get_attribute("href") if await page.get_by_role("link", name="Download MP4", exact=True).count() == 1 else None
                    await page.get_by_role("button", name="Generate updated campaign", exact=True).or_(
                        page.get_by_role("button", name="Generate campaign", exact=True)).click()
                    deadline = time.monotonic() + args.timeout_seconds
                    while True:
                        link = page.get_by_role("link", name="Download MP4", exact=True)
                        if await link.count() == 1 and await link.get_attribute("href") != old:
                            break
                        if await page.get_by_role("button", name="Retry failed stage", exact=True).count():
                            raise RuntimeError("Campaign failed; evidence retained. Automatic rerender is disabled.")
                        if time.monotonic() >= deadline:
                            raise TimeoutError("Campaign did not produce a new output before its deadline")
                        await asyncio.sleep(3)
                        refresh = page.get_by_role("button", name="Refresh", exact=True)
                        if await refresh.is_enabled():
                            await refresh.click()
                await page.screenshot(path=str(directory / "campaign.png"), full_page=True)
                link = page.get_by_role("link", name="Download MP4", exact=True)
                await link.wait_for(state="visible")
                async with page.expect_download() as downloaded:
                    await link.click()
                download = await downloaded.value
                await download.save_as(directory / "video.mp4")
                failures = await asyncio.to_thread(inspect_video, directory, seconds)
                record.update(technical_status="failed" if failures else "passed", findings=failures)
            except Exception as exc:
                record["error"] = str(exc)
                await page.screenshot(path=str(directory / "failure.png"), full_page=True)
            finally:
                (directory / "result.json").write_text(json.dumps(record, indent=2))
                results.append(record)
                gallery(root)
            if record["technical_status"] != "passed":
                # Stop on the first failure; do not spend more credits on a broken workflow.
                break
        await context.close()
        await browser.close()
    return 0 if all(r["technical_status"] == "passed" for r in results) else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    g = sub.add_parser("gallery")
    g.add_argument("output")
    b = sub.add_parser("browser")
    b.add_argument("--property-url", required=True)
    b.add_argument("--storage-state", required=True, help="Private Playwright login state; never commit or upload")
    b.add_argument("--output", required=True)
    b.add_argument("--headed", action="store_true")
    b.add_argument("--generate", action="store_true", help="Explicit opt-in to provider charges and account campaign usage")
    b.add_argument("--max-new-campaigns", type=int, default=0)
    b.add_argument("--timeout-seconds", type=int, default=900)
    args = parser.parse_args()
    if args.command == "gallery":
        records = gallery(Path(args.output))
        return 0 if all(r["technical_status"] == "passed" for r in records) else 1
    return asyncio.run(browser_run(args))


if __name__ == "__main__":
    raise SystemExit(main())
