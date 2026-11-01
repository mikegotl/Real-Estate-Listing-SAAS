## Prompt 10: Performance measurement and recommendation feedback

**Manual entry and CSV only.** Nothing signs in to, scrapes, reads from or publishes to TikTok, Instagram or YouTube. The agent pastes the post link and copies numbers from the platform's own analytics; leads and booked showings are counted by the agent.

**New entities** (`ListingStudio.Domain.Campaigns.CampaignPerformance`):
- `CampaignDistributionRecord`: one posting of one library video. It stores:
  - workspace, property, campaign, variant and deliverable
  - platform (`TikTok`, `InstagramReels`, `YouTubeShorts`, stored by name)
  - post URL and publication time
  - The link must be https on the platform's own site (`tiktok.com`, `instagram.com`, `youtube.com` / `youtu.be`, subdomains allowed, look-alike hosts refused). The query string and fragment are dropped, so tracking parameters never reach the database. A link is recorded once per workspace.
- `CampaignMetricSnapshot`: an append-only reading. It stores:
  - views, likes, comments, shares, saves
  - average watch seconds and completion rate (optional: blank when the platform doesn't show them)
  - clicks, leads, booked showings
  - capture time (to the second), source (`Manual` or `CsvImport`) and who entered it
  - A database trigger rejects UPDATE and DELETE, and a unique index on (post, capture second) rejects duplicates.
  - Validation:
    - no negative numbers
    - likes, comments, shares, saves and clicks can't exceed views
    - completion rate 0–100%, watch time 0–3,600 s
    - capture time between publication and now
- `VerticalRuleEvaluationReport`: a draft evaluation with:
  - base rule version and calculation version
  - video count, total views and segment JSON (counts and rates only, no listing text, URLs or user ids)
  - minimum-sample flag, and promotion fields

**Calculation (`performance-calc-v1`, `VerticalPerformanceCalculator`).**
- Each post contributes its latest snapshot, and a video sums its posts. Raw totals are kept beside the rates.
- Rates:
  - like, share and save rate (per view)
  - completion rate (view-weighted across the posts that report it)
  - click-through rate (clicks per view)
  - lead conversion (leads per click)
- Relative uplift is each video against the average of the other measured videos in the same group: one campaign's library on the Campaign tab, or one creative test (same property, same suite) on the admin tab.
- **Awareness and business outcomes are separate.**
  - The awareness leader has the most views (then like rate).
  - The business leader has the most leads, then booked showings, then clicks.
  - Both are marked, so a video with fewer likes but more leads stays visible as "Most leads" (tested in the calculator, the service and both screens).

**Campaign tab.** A "How these videos are doing" panel appears under the vertical video library:
- **Comparison table.** "Reach and engagement" columns sit apart from the shaded "Business results" columns. Arrows show uplift against the others, and videos without numbers say so.
- **Add a post:** video, platform, link, posted time.
- **Add numbers:** post, time, and every metric.
- **Import a CSV:**
  - Columns, in any order: platform, post_url, captured_at, views, likes, comments, shares, saves, average_watch_seconds, completion_rate, clicks, leads, booked_showings.
  - completion_rate accepts 0.42 or 42%. Numbers like "1,200" are fine. Limits: 1 MB and 2,000 rows.
  - It's all or nothing: every problem is listed by line (bad value, unknown post, platform mismatch, duplicate row, reading already stored) and nothing is imported until all are fixed.
- A read-only campaign shows the table without forms. The panel doesn't render for legacy campaigns.

**Admin Creative format testing tab.**
- **"Published test performance"** shows one comparison table per creative test with numbers.
- **"Published tests with metrics"** now counts the creative test videos with recorded numbers. It was always 0 in Prompt 9.
- **Rule evaluation panel:**
  - **Create draft report** segments every measured library video in the admin's workspace by:
    - format
    - property type
    - price band (under $300k, $300k–$600k, $600k–$1M, $1M and up)
    - media availability (12+ photos or not, with or without a property video)
    - agent account, labelled Agent 1, Agent 2… so no name, email or id is stored
  - Each segment shows its videos, views, like rate, click rate, leads and the average rule score the recommendation engine gave those videos.
  - **Render regression campaigns are excluded** (jobs referenced by `TikTokTestSuites`), as are legacy campaigns.
  - **Documented minimum sample** (`VerticalPerformanceCalculator`):
    - at least 30 measured videos in the report
    - every format in it needs at least 10 measured videos and 5,000 views
  - **Promotion** is a separate, explicit step:
    - It needs a platform admin, a draft that meets the minimum sample, and a rule version the engine implements. Today that is only `vertical-rules-v1`.
    - A report can be promoted once.
    - The active rule version shows on the panel.
  - **Reports never change recommendation weights**, and nothing changes them automatically. A new rule version still has to be written as code before it can be promoted.

### Flag, schema, queue and rollback
- **Flag off (kill switch or workspace not in the pilot list):**
  - the Campaign tab panel and every admin measurement section are hidden (the admin tab itself is already hidden)
  - every `IVerticalPerformanceService` and `IVerticalRuleEvaluationService` call throws `PerformanceTrackingDisabledException`: view, add post, add numbers, import, suite performance, draft, list, promote
  - posts, snapshots and reports are never deleted, and reappear when the flag is turned back on (tested)
- **Schema:** additive migration `AddVerticalPerformanceMetrics` creates three tables, `CampaignDistributionRecords`, `CampaignMetricSnapshots` and `VerticalRuleEvaluationReports`, plus the append-only trigger and function. Foreign keys from posts to variants and deliverables, and from snapshots to posts, use Restrict, so nothing cascades away. Down is development only.
- **Queued work:** none. Nothing runs in the background; every write is a direct user action.
- **Rollback:** switch the flag off. Old code ignores the new tables. Recorded numbers stay.

### Files
- Domain: `Campaigns/CampaignPerformance.cs`
- Application: `Campaigns/VerticalPerformance.cs` (service interfaces, views, calculator, CSV parser)
- Infrastructure:
  - `Campaigns/VerticalPerformanceService.cs`
  - `Persistence/Configurations/CampaignPerformanceConfiguration.cs`
  - DbSets and DI registrations
  - migration `20261101015629_AddVerticalPerformanceMetrics`
- Web:
  - `Properties/CampaignPerformancePanel.razor` (+ css)
  - `Properties/VerticalPerformanceTable.razor` (+ css)
  - `CampaignGenerationManager.razor`
  - `Administration/AdminCreativeTestingTab.razor` (+ css)
- Tests:
  - `UnitTests/VerticalPerformanceTests.cs`
  - `UnitTests/CampaignPerformancePanelTests.cs`
  - `UnitTests/AdminCreativeTestingTabTests.cs` (extended)
  - `IntegrationTests/VerticalPerformanceTests.cs`

### Commands run
- `dotnet build ListingStudio.slnx`: 0 warnings. `migrations has-pending-model-changes`: no changes.
- New unit tests (`VerticalPerformanceTests`, 14 including theory rows):
  - link normalization and rejection (http, look-alike host, wrong platform)
  - metric validation and capture to the second
  - calculation accuracy, view-weighted completion and uplift
  - reach and business leaders kept apart
  - CSV parsing (quotes, thousands separators, percentages, any column order)
  - CSV errors per line, duplicate rows and missing columns
  - minimum sample per format and per report
  - promotion once, only with the sample and only for implemented versions
- New bUnit tests:
  - `CampaignPerformancePanelTests`, 4:
    - business winner visible
    - manual post and numbers entry, with error shown
    - read-only view
    - hidden when switched off
  - `AdminCreativeTestingTabTests`, +1: published count, test comparison, below-minimum report has no Promote, draft and promote calls
- New integration tests:
  - `PerformanceIsRecordedPerLibraryVideoComparedWithinTheTestAndKeptWhenSwitchedOff`:
    - a real hook A/B creative test, then posts on both videos
    - unknown, look-alike and duplicate posts refused
    - two snapshots with the latest one used; a duplicate second and invalid values refused
    - CSV with three bad lines imports nothing, the good file imports, and a re-import is a duplicate
    - database trigger blocks UPDATE and DELETE
    - suite comparison: hook A reach, hook B leads
    - draft below the minimum sample can't be promoted, and an unimplemented version is refused
    - a regression suite pointing at one of the campaigns drops it from the evaluation
    - another workspace's admin sees and changes nothing
    - kill switch refuses every call and keeps every row; it all comes back when switched on
  - `OnlyPlatformAdminsRunTheRuleEvaluation`
- Full run `RUN_FFMPEG_E2E=1 dotnet test ListingStudio.slnx`: 604 unit and 240 integration tests passed. The regression-exclusion step was then added to the performance integration test, and that test passed on its own run.
- Screenshots (desktop and phone): project files `reviews/2026-11-01-vertical-prompt-10/`.

### Remaining risks
- Numbers are only as good as what agents type or export. There is no platform verification, by design for this version.
- Uplift compares against the simple average of the other videos in the group. With two videos it is a plain A-versus-B ratio and says nothing about significance. The minimum sample applies only to rule promotion, not to the per-campaign table.
- Agent labels in a report are stable only within that report (ordered by user id), so "Agent 1" can be a different person in two reports.
- "Media availability" counts the photos in the storyboard (Extra photos excluded) and whether any property video exists at report time, not at render time.
- Promotion records the active rule version, but with only `vertical-rules-v1` implemented it can't change recommendations yet. A v2 needs code and its own review.
