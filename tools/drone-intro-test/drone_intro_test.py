#!/usr/bin/env python3
"""Drone arrival intro test: reverse pull-back from a real front photo.

For each photo: upload it to Higgsfield, ask Kling 3.0 for a rising pull-back to about 50 ft
(only a first frame, no last frame), download the clip, play it backwards so it lands on the
real photo, burn in the AI label, then hold the untouched photo for 1.5 s.

Uses the same Higgsfield endpoints and auth as HiggsfieldWalkthroughVideoProvider.
Needs Python 3.9+ and ffmpeg. No pip packages.

    export HIGGSFIELD_API_KEY=...  HIGGSFIELD_API_SECRET=...
    python3 tools/drone-intro-test/drone_intro_test.py path/to/front-photo.jpg --out results

With no photos it uses the landing-page stock home in src/ListingStudio.Web/wwwroot/images.

Re-run only the reverse/label step on clips you already downloaded:
    python3 tools/drone-intro-test/drone_intro_test.py path/to/front-photo.jpg --out results --postprocess-only
"""
import argparse
import json
import mimetypes
import os
import shutil
import subprocess
import sys
import time
import urllib.request
from pathlib import Path

DEFAULT_MODEL = "kling-video/v3.0/std/image-to-video"
DEFAULT_PHOTO = Path(__file__).resolve().parents[2] / "src/ListingStudio.Web/wwwroot/images/hero-home.webp"
LABEL = "AI-generated aerial · not actual footage"

PROMPT = """Real-estate aerial drone shot. The camera starts exactly on this photograph of the front of the house,
then smoothly rises and pulls back over the front yard toward the street, climbing to about {height} feet,
tilting down slightly so the house stays centered in frame.
Keep the facade, windows, doors, garage, colors, driveway and landscaping exactly as in the photograph.
Show only a little of the roof. Do not add a swimming pool, water, a view, solar panels, extra buildings,
people, moving cars or text. Steady, smooth, realistic drone footage. No camera shake."""


def api(base, key, secret, method, path, body=None):
    request = urllib.request.Request(
        f"{base.rstrip('/')}/{path}",
        method=method,
        data=None if body is None else json.dumps(body).encode(),
        headers={"Authorization": f"Key {key}:{secret}", "Content-Type": "application/json"},
    )
    with urllib.request.urlopen(request, timeout=120) as response:
        return json.loads(response.read() or b"{}")


def upload(base, key, secret, photo):
    content_type = mimetypes.guess_type(photo.name)[0] or "image/jpeg"
    target = api(base, key, secret, "POST", "files/generate-upload-url", {"content_type": content_type})
    headers = {k: v for k, v in (target.get("upload_headers") or {}).items() if k.lower() != "content-type"}
    headers["Content-Type"] = content_type
    put = urllib.request.Request(target["upload_url"], method="PUT", data=photo.read_bytes(), headers=headers)
    urllib.request.urlopen(put, timeout=120).close()
    return target["public_url"]


def generate(args, photo, raw_path):
    key, secret = os.environ["HIGGSFIELD_API_KEY"], os.environ["HIGGSFIELD_API_SECRET"]
    image_url = upload(args.base_url, key, secret, photo)
    queued = api(args.base_url, key, secret, "POST", args.model, {
        "prompt": PROMPT.format(height=args.height),
        "image_url": image_url,
        "duration": args.duration,
        "sound": "off",
    })
    request_id = queued["request_id"]
    print(f"  queued {request_id}")
    deadline = time.time() + args.timeout_minutes * 60
    while time.time() < deadline:
        time.sleep(10)
        status = api(args.base_url, key, secret, "GET", f"requests/{request_id}/status")
        state = (status.get("status") or "").lower()
        print(f"  {state}")
        if state == "completed":
            urllib.request.urlretrieve(status["video"]["url"], raw_path)
            return request_id
        if state in ("failed", "nsfw", "canceled", "cancelled"):
            raise RuntimeError(f"Higgsfield returned {state} for {photo.name}")
    raise TimeoutError(f"{photo.name} did not finish in {args.timeout_minutes} minutes")


def probe_size(path):
    out = subprocess.check_output([
        "ffprobe", "-v", "error", "-select_streams", "v:0",
        "-show_entries", "stream=width,height", "-of", "csv=p=0", str(path)], text=True)
    width, height = out.strip().split(",")[:2]
    return int(width), int(height)


def drawtext_escape(text):
    return text.replace("\\", "\\\\").replace(":", "\\:").replace("'", "\\'")


def postprocess(args, photo, raw_path, final_path, sheet_path):
    width, height = probe_size(raw_path)
    width, height = width - width % 2, height - height % 2
    font = f"fontfile='{args.font}':" if args.font else ""
    label = (
        f"drawtext={font}text='{drawtext_escape(LABEL)}':fontcolor=white:fontsize=h/30:"
        "box=1:boxcolor=black@0.55:boxborderw=12:x=w*0.04:y=h-th-h*0.06"
    )
    graph = (
        f"[0:v]reverse,scale={width}:{height},setsar=1,fps=30,format=yuv420p,{label}[drone];"
        f"[1:v]scale={width}:{height}:force_original_aspect_ratio=increase,crop={width}:{height},"
        f"setsar=1,fps=30,format=yuv420p[hold];"
        "[drone][hold]concat=n=2:v=1:a=0[v]"
    )
    subprocess.run([
        "ffmpeg", "-loglevel", "error", "-y", "-i", str(raw_path),
        "-loop", "1", "-t", "1.5", "-i", str(photo),
        "-filter_complex", graph, "-map", "[v]", "-c:v", "libx264", "-crf", "18",
        "-movflags", "+faststart", str(final_path)], check=True)
    # Six evenly spaced frames, left to right, top to bottom.
    duration = float(subprocess.check_output([
        "ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", str(final_path)], text=True))
    subprocess.run([
        "ffmpeg", "-loglevel", "error", "-y", "-i", str(final_path),
        "-vf", f"fps=6/{duration:.3f},scale=640:-2,tile=3x2:padding=6:color=white",
        "-frames:v", "1", str(sheet_path)], check=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("photos", nargs="*", type=Path, help="Front exterior photos (default: the stock home)")
    parser.add_argument("--out", type=Path, default=Path("results"))
    parser.add_argument("--height", type=int, default=50, help="Target drone height in feet (default 50)")
    parser.add_argument("--duration", type=int, default=5, help="Clip length in seconds (default 5)")
    parser.add_argument("--model", default=os.environ.get("HIGGSFIELD_MODEL", DEFAULT_MODEL))
    parser.add_argument("--base-url", default=os.environ.get("HIGGSFIELD_BASE_URL", "https://api.higgsfield.ai"))
    parser.add_argument("--timeout-minutes", type=int, default=15)
    parser.add_argument("--font", help="TTF font for the label (ffmpeg default font if omitted)")
    parser.add_argument("--postprocess-only", action="store_true", help="Reuse existing *-raw.mp4 files")
    args = parser.parse_args()

    if not shutil.which("ffmpeg"):
        sys.exit("ffmpeg is required.")
    if not args.postprocess_only and not (os.environ.get("HIGGSFIELD_API_KEY") and os.environ.get("HIGGSFIELD_API_SECRET")):
        sys.exit("Set HIGGSFIELD_API_KEY and HIGGSFIELD_API_SECRET first.")

    args.photos = args.photos or [DEFAULT_PHOTO]
    args.out.mkdir(parents=True, exist_ok=True)
    summary = []
    for photo in args.photos:
        stem = f"{photo.stem}-{args.height}ft"
        raw = args.out / f"{stem}-raw.mp4"
        print(f"{photo.name}:")
        entry = {"photo": photo.name, "height_ft": args.height, "model": args.model}
        try:
            if not args.postprocess_only:
                entry["request_id"] = generate(args, photo, raw)
            postprocess(args, photo, raw, args.out / f"{stem}.mp4", args.out / f"{stem}-sheet.jpg")
            entry["result"] = f"{stem}.mp4"
        except Exception as error:  # keep going so one bad photo doesn't stop the batch
            entry["error"] = str(error)
            print(f"  error: {error}")
        summary.append(entry)
    (args.out / "summary.json").write_text(json.dumps(summary, indent=2))
    print(f"Done. Results in {args.out}/")


if __name__ == "__main__":
    main()
