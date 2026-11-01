## Prompt 9: TikTok Creative Testing Studio (admin)

**Two tabs on /admin/campaign-tests, only while `VerticalVideoLibraryV1` is on for the admin's workspace.**
- The page is titled "TikTok Creative Testing Studio".
- **Creative format testing** is the first tab. **Vertical render regression testing** holds the existing gallery and the three fixed cases (Short voiceover, Music and text, Full voiceover), unchanged in purpose.
- `?tab=regression` opens the second tab directly. With the switch off the page is the old "TikTok review studio", byte for byte. `?tab=creative` falls back to it and no creative markup is rendered (tested).

**Creative format testing.**
1. Pick a property in the admin's workspace and run the recommendation engine. Every format shows:
   - score, strength, purpose, reasons, what's missing, length and recipe version
   - hook A and, where one exists, hook B
2. Choose what to generate:
   - the recommended three
   - every format (ones the listing can't support become Skipped cases with what's missing)
   - one selected format
   - hooks A and B for one format
3. The estimate shows before starting: how many test campaigns will run, how many are skipped, and that each one uses generation credits and counts toward the allowance like a normal campaign.

**Hook B.** `VerticalHookVariants.ApplyB` gives different opening words around the same grounded fact, or a fixed approved template:
- Price first: "What $450,000 gets you in Raleigh, NC"
- Wow feature: "Wait until you see this kitchen."
- Hosted POV: "Come take a look with me."
- Story reveal: "Watch how this one turns out."

Exclusive access has no hook B, because any rewording of an access line would be a new access claim. Hook B is only for one chosen format on admin test campaigns: `CampaignGenerationJob.VerticalHookVariant = "b"`. The variant records hook "b", and the caption hook follows it.

**New entities** (instead of more columns on `TikTokTestSuite`):
- `VerticalCreativeTestSuite`: mode, format, rule version, status.
- `VerticalCreativeTestCase`, one per video. It stores:
  - format, hook variant, score, reasons JSON, recipe version and expected duration
  - job id, variant id and deliverable id
  - status (Pending, Running, Passed, Failed, Skipped, Cancelled), error, and created, started, finished and updated times

All enums are stored by name.

**Worker.**
- `VerticalCreativeTestWorker` is a new hosted service. It has its own advisory lock and queue, so it never waits on or blocks the regression suite.
- Cases run strictly one after another, with deterministic case keys (one key per suite and index). A crashed start finds the campaign it already queued.
- Each case is an ordinary private TikTok campaign that makes exactly one library video (AgentChosen with one format, social defaults, no paid AI walk-through clips).
- When it finishes, the case validates its own deliverable:
  - the expected recipe version and length
  - 9:16 output and file health (stored size matches the rendered size)
  - a full ffprobe and decode through `IRenderedVideoValidator`
- A failure records the technical findings and the suite carries on. Earlier passed cases are never touched.
- A format the listing can't support (at start, or when the campaign is planned) is Skipped, not Failed.
- One active creative test per workspace; one active regression suite per workspace stays separate.
- Revoked admin rights cancel the suite before it spends anything. Cancel stops the remaining cases and asks the running campaign to stop; finished videos stay.

**Gallery.**
- Cards are grouped by test and property, keyed by deliverable id. Each card shows:
  - format, hook, purpose, score, reasons and recipe version
  - technical status and findings, and the editorial decision
  - the player, Download MP4 and a link to the property campaign
- Review drafts are keyed by deliverable and version, and approve or request changes go through `DecideDeliverableAsync`. The chat panel targets the deliverable.
- The regression gallery also keys cards, players and chat by deliverable now.

**Dashboard.** The creative tab shows four counts: awaiting creative review, technical failures, changes requested, and published tests with metrics. The last one is always 0 until Prompt 10 adds performance data.

**Isolation.** The regression gallery (`ICampaignReviewService.ListAsync`) excludes every campaign a creative case made. Regression cases still make the legacy shared vertical edit (a test key without a library request never uses the library).

### Flag, schema, queue and rollback
- **Flag off (kill switch or workspace not in the pilot list):**
  - the creative tab is not rendered
  - every `IVerticalCreativeTestService` call (preview, start, cancel, list) throws `CreativeTestingDisabledException`
  - the worker skips that workspace's suites, so queued cases never start a campaign
  - stored suites, cases, videos and reviews are untouched and reappear when it's turned back on (tested)
  - the regression suite works as before (tested)
- **Schema:** additive migration `AddVerticalCreativeTests`: tables `VerticalCreativeTestSuites` and `VerticalCreativeTestCases`, and nullable `CampaignGenerationJobs.VerticalHookVariant` (8). Down is development only.
- **Queued work:** a new hosted worker polls every 5 s. Turning the switch off pauses queued cases; a running case's campaign follows the existing library kill switch (it falls back or stops), and the case then records that.
- **Rollback:** switch off. Old code ignores the new tables and column. Removing the worker leaves queued cases idle.

### Commands run
- `dotnet build ListingStudio.slnx`: 0 warnings. `migrations has-pending-model-changes`: no changes.
- New unit tests (`VerticalCreativeTestingTests`, 6):
  - hook B rewording and validation
  - no hook B for exclusive access
  - case planning per mode, with skips for formats the listing can't support
  - sequential cases where a failure keeps earlier passes
  - cancel keeps finished cases
  - hook B only allowed for one chosen format
- New bUnit tests (`AdminCreativeTestingTabTests`, 3):
  - grounded preview, estimate and start arguments
  - suite grouping, technical findings, and review by deliverable and version
  - the switched-off message
- New integration tests:
  - `CreativeHookTestMakesAndValidatesOneVideoPerCaseOutsideTheRegressionGallery`:
    - Price first hooks A and B as two campaigns
    - both pass their own validation; hook B's video carries hook B
    - independent decisions and dashboard counts
    - kept out of the regression gallery
    - kill switch refuses every call, keeps the evidence, and restores it
  - `KillSwitchStopsQueuedCreativeCasesFromStartingAndLeavesTheRegressionSuiteUsable`:
    - global off beats the pilot list
    - no campaign is created and the regression suite starts and cancels
    - back on, the case starts an AgentChosen one-format campaign
  - `CreativeTestsNeedAPlatformAdminAndStayInTheirWorkspace`
  - `AdminCampaignReviewPageTests` extended for the tabs with the switch on and the old page with it off.
- Full run `RUN_FFMPEG_E2E=1 dotnet test ListingStudio.slnx`: 585 unit and 238 integration tests passed.
- Screenshots (desktop and phone): project files `reviews/2026-11-01-vertical-prompt-9/`.

### Remaining risks
- Each creative case is its own campaign, so "the recommended three" costs three campaigns. The shared narration is reused, but the allowance is counted per campaign. The estimate says so.
- Hook B changes the on-screen and caption hook only. The voiceover still reads the format's own lines.
- While photo analysis is pending, a picked Wow feature or Price first case waits instead of being skipped. If analysis then finds the format ineligible, the case is Skipped when its campaign is planned.
- The worker reads the feature switch through the 15 s cache, so a case can start up to 15 s after the switch is turned off.
- Screenshots come from the component rendered with the app theme, not a signed-in browser session.
