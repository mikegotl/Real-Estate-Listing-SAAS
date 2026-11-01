## Prompt 8: review, revisions and downloads for several vertical videos

**Results.** When a campaign made a vertical video library, the Campaign tab shows a **Vertical video library** section above the other files. It has one card per video with:
- format, purpose (the posting objective) and the recommendation score, strength and reasons
- hook, length, recipe version, a technical status chip (Waiting, Passed, Failed, Skipped, Cancelled) and an editorial decision chip
- the player, the caption, Download MP4, Download caption and Copy caption
- the review box, review history and the video's own change-request chat

A video that failed shows why it couldn't be made, and the rest of the library still shows (partial completion). Legacy single-video campaigns render nothing new.

**Review identity is now DeliverableId plus Version.**
- New `ICampaignVideoLibraryService.DecideAsync(user, deliverableId, expectedVersion, …)`. A decision for a version that is no longer current is refused.
- `ICampaignReviewService.DecideDeliverableAsync` is added. The old `DecideAsync(jobId, kind)` still works: it finds the deliverable and goes through the same rules (`CampaignVideoReviewRules`).
- Admin review drafts and decisions on /admin/campaign-tests are keyed by deliverable and version.
- Video URLs carry the version: `/campaigns/videos/{deliverableId}?v={version}`. An old version's link returns 404, so a stale link never plays the wrong cut. The legacy `/campaigns/{jobId}/deliverables/{kind}` route is unchanged.

**Independent review and revisions.**
- Each video is approved or sent back on its own. Approving one leaves the others as they are.
- The per-video chat now targets a deliverable (`ICampaignVideoChatByVideo`). A library video's revision renders to `/variants/{id}/v{n}/` and uses that variant's own captions.
- A replacement version starts with no decision, so it needs a fresh review. Unrelated videos keep their approvals.
- Undo goes back to the earlier version, and that version's earlier decision applies again.

**Downloads.**
- Per video: `/campaigns/videos/{id}?v=&download=true` gives `{property}-{format}-v{n}.mp4`, and `/campaigns/videos/{id}/caption` gives the caption text.
- Whole library: `/campaigns/{jobId}/library.zip` holds every rendered video, its caption file and `manifest.json` (`vertical-library-manifest-v1`).
- The manifest lists, per video: format, purpose, hook, CTA, file names, recipe version, intended platforms (TikTok, Instagram Reels, YouTube Shorts), technical validation result and editorial decision. It has no URLs, storage paths, ids or secrets.
- The ZIP is built in a temporary file and then streamed, because ASP.NET Core refuses the synchronous writes that ZipArchive makes.
- Every route requires sign-in and is scoped to the caller's workspace. Another workspace gets 404.

### Flag, schema, queue and rollback
- **Flag off:** no library jobs are created, so no library section shows and the new routes find nothing to serve. Libraries already made while the flag was on stay viewable and downloadable to their workspace, so finished work isn't hidden.
- **Schema:** none. A navigation property `CampaignDeliverable.CampaignVideoVariant` was added on an existing relationship; `has-pending-model-changes` reports no changes.
- **Queued work:** revisions of library videos use the existing revision queue. A revision of a variant writes a new versioned file and never overwrites the earlier one.
- **Rollback:** revert the commit. No data to clean up. Decisions recorded by deliverable are in the existing review table, which old code already reads by deliverable.

### Commands run
- `dotnet build ListingStudio.slnx`: 0 warnings. `migrations has-pending-model-changes`: no changes.
- Targeted: `LibraryVideosAreReviewedRevisedAndDownloadedOneAtATime`, `LibraryRoutesServeOnlyTheCurrentVersionToTheWorkspace`, `FailedVideoIsMarkedFailed…` (extended for the partial library view and ZIP): 3 passed. `CampaignVideoLibraryResultsTests` (bUnit, 4): passed.
- Full run `RUN_FFMPEG_E2E=1 dotnet test ListingStudio.slnx`: 576 unit and 235 integration tests passed.

**New tests**
- `LibraryVideosAreReviewedRevisedAndDownloadedOneAtATime` covers:
  - independent decisions
  - a stale version refused
  - a revision through the per-video chat rendering v2 at the variant path
  - the new version needing a fresh review while the other videos keep theirs
  - the stale link returning nothing
  - caption text, ZIP contents and manifest accuracy
  - another workspace denied
  - undo back to v1
- `LibraryRoutesServeOnlyTheCurrentVersionToTheWorkspace` is an HTTP test. It checks the MP4 and file name, a stale version returning 404, the caption, the ZIP content type, and another workspace getting 404 for the video and the ZIP.
- `CampaignVideoLibraryResultsTests` covers:
  - multiple cards with their chips and versioned links
  - a failed card with no player or review
  - Approve sending that video's id and current version
  - read-only properties
  - legacy campaigns rendering nothing

### Remaining risks
- The legacy `/campaigns/{jobId}/deliverables/{kind}` route is unchanged, but it has no new HTTP-level test. Its service is covered by the existing campaign tests.
- No screenshots of the results section yet. Prompt 9 screenshots will include the admin gallery, which reuses the same per-deliverable review.
- Copy caption uses the browser clipboard, which needs a secure page (it does on the production domain).
- The technical status shows Passed for every rendered library video because a video that fails validation is recorded as Failed. No separate re-check runs when the card opens.
