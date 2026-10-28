# Drone arrival intro test

This is a one-off test of the optional "drone arrival" intro for the Exterior front stop. It is not used by the app.

For each front photo, the script:

1. Uploads the photo to Higgsfield. It uses the same endpoints and auth as `HiggsfieldWalkthroughVideoProvider`, and the model `kling-video/v3.0/std/image-to-video`.
2. Asks for a rising pull-back to about **50 ft**, with the real photo as the first frame only.
3. Plays the clip backwards, so it descends and lands exactly on the real photo.
4. Burns in "AI-generated aerial · not actual footage", then holds the untouched photo for 1.5 s.

## Run

You need Python 3.9 or newer and ffmpeg. No pip packages are required. Keys come only from environment variables, so never commit them.

```
export HIGGSFIELD_API_KEY=...      # same values as the GitHub "production" secrets
export HIGGSFIELD_API_SECRET=...
python3 tools/drone-intro-test/drone_intro_test.py path/to/front1.jpg path/to/front2.jpg --out tools/drone-intro-test/results
```

With no photo arguments, it uses the landing-page stock home (`src/ListingStudio.Web/wwwroot/images/hero-home.webp`). Keep private listing photos in `tools/drone-intro-test/photos/`, which is git-ignored, like `results/`.

Useful options:

- `--height 100` compares a higher start.
- `--duration 5` sets the clip length in seconds.
- `--model` and `--base-url` override the defaults, or set `HIGGSFIELD_MODEL` / `HIGGSFIELD_BASE_URL`.
- `--font /path/to/font.ttf` sets the label font if ffmpeg has no default font.
- `--postprocess-only` re-runs the reverse and label step on clips already downloaded.

Each call is about one 5 s Kling clip, roughly $0.40 to $1.65. That figure comes from third-party pricing pages and is unverified.

## Output in `results/`

- `<photo>-50ft.mp4`: the final intro
- `<photo>-50ft-raw.mp4`: the clip as the AI returned it
- `<photo>-50ft-sheet.jpg`: six frames
- `summary.json`: request IDs and any errors

## What to judge

1. Does it read as a drone descending, or as a zoom?
2. What did it invent? Check the roof, the yard, the neighbors, the street, and any pool or water.
3. Does the last AI frame meet the real photo without a visible jump?
