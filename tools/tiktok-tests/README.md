# TikTok campaign testing — initial three cases

The matrix covers short voiceover / fast cuts / 30 seconds, music and text only /
fast cuts / 30 seconds, and full voiceover / cinematic glide / 60 seconds.
It is the first regression slice, not exhaustive coverage of the campaign options.

## CI rendering worker

Normal CI runs `TikTokReviewOutputsArePlayable` with the real `ShortFormEditor` and
FFmpeg renderer. Inputs are synthetic images and audio tones; there are no paid
provider calls, customer photos, or browser login credentials. The existing
`CampaignGenerationTests` continue covering the service workflow, tenant access,
retries, timeout handling and output selection. These are separate scopes: the
synthetic render test does not prove the complete browser/provider workflow.

Every run retains three MP4s, sampled-frame contact sheets, specifications and
technical reports in a `tiktok-review-…` GitHub Actions artifact for 14 days. Download
and extract it, then open `index.html` to watch the outputs. Technical validation
checks dimensions, codecs, frame rate, duration, narration-plan presence and full
file decoding. Editorial status always remains pending human review; test tones
cannot verify speech, pronunciation, captions or the accuracy of generated facts.

Local renderer run (FFmpeg, ffprobe and .NET prerequisites from the root README):

```sh
RUN_FFMPEG_E2E=1 TIKTOK_REVIEW_DIR="$PWD/artifacts/tiktok-review" \
  dotnet test tests/ListingStudio.IntegrationTests --filter FullyQualifiedName~TikTokReviewOutputsArePlayable
python3 tools/tiktok-tests/review.py gallery artifacts/tiktok-review
```

Use a fresh artifact directory for every run. The renderer deliberately refuses
to overwrite existing MP4s.

## Live browser worker

The browser command walks Overview → Story & script → Campaign, downloads the
video, validates it with ffprobe and FFmpeg, and produces the same local gallery.
It reads the existing output by default. To install the optional browser dependency:

```sh
python3 -m venv tools/tiktok-tests/.venv
tools/tiktok-tests/.venv/bin/pip install -r tools/tiktok-tests/requirements.txt
tools/tiktok-tests/.venv/bin/playwright install chromium
```

Create a private Playwright storage-state file through an interactive login using
`playwright codegen --save-storage=<private-path> <property-url>`. Treat that file
as a password: never commit it or include it in artifacts. Then audit an existing
output without changing campaign settings:

```sh
tools/tiktok-tests/.venv/bin/python tools/tiktok-tests/review.py browser \
  --property-url '<signed-in-property-url>' --storage-state '<private-path>' \
  --output '<fresh-private-results-directory>'
```

To authorize up to three new campaign renders, append `--generate --max-new-campaigns 3`.
This spends provider credits and account campaign allowance. The limit counts
campaign requests, not provider requests or dollars. Each case selects only the
vertical deliverable, balanced showcase, modern ambient music, and disables AI
presenter, portrait and walkthrough footage for a controlled photos-only baseline.
It leaves the existing language and region choices unchanged. It never confirms
new portrait consent, posts to TikTok, purchases a plan, retries failed generation,
or publishes a lead page. It stops at the first failed case and retains evidence.
Campaign settings on this property are changed; use a dedicated test property.

Live results contain private property media and screenshots. Keep them private;
the public repository's CI artifact uploader is for synthetic test data only.
Live browser automation has to be validated against the deployed UI before being
scheduled. No recurring live job or autonomous code-fixing agent is installed by
this initial change. A durable runner, protected test login, failure-to-fix handoff
and regression expansion are follow-up work.
