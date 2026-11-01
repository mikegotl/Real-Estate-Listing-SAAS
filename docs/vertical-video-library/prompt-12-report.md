## Prompt 12: Rollout and acceptance (release report)

**Nothing was merged, squashed, deployed or cleaned up.** No paid provider was called or enabled; every check below ran with the test suite's fake providers. The feature is ready for a controlled pilot once the owner decides (last section).

### What Prompt 12 added
- **Release switch page** at `/admin/features` (platform admins only; "Release switches" on the admin dashboard). It edits `VerticalVideoLibraryV1` through the one central `IFeatureStateService`:
  - a global switch (the kill switch: off beats everything)
  - an "every workspace" switch
  - a pilot list of workspaces, with an "Add tester and admin workspaces" button that suggests every workspace with a Tester or platform admin member, and a search box (2+ characters, top 20) for any other workspace
  - a plain summary of what Save will do ("On for 2 pilot workspaces.")
  - Every save writes a `PlatformAdminAuditEvent` (`platform-admin.features.configure`, target the flag key, outcome like `global=1 all=0 pilots=2`; pseudonymous actor). Unknown workspace ids are refused.
- **Tests:**
  - `VerticalRolloutTests`: flag-off equivalence on a database that already has libraries, variants, creative test cases, posts, metric snapshots and a rule report; and the release page (admin only, suggestions, audit, pilot precedence).
  - `PreFeatureMigrationTests`: upgrade from the last pre-feature migration with legacy rows, and migration of an empty database.
  - `VerticalAcceptanceTests`: the four listing profiles, and the measured cost of the three-video default.

### Acceptance evidence
- Restore and `dotnet build ListingStudio.slnx`: 0 warnings, 0 errors.
- Full run `RUN_FFMPEG_E2E=1 dotnet test ListingStudio.slnx` (all unit tests, all PostgreSQL integration tests on Testcontainers, and the FFmpeg/ffprobe end-to-end matrix): **605 unit and 247 integration tests passed**.
- The release page's bUnit tests (`AdminFeaturesTests`, 3) and one page fix (keep the every-workspace choice when the kill switch is turned off) were added after that run. The unit project was rebuilt and its tests passed: 608. The integration tests don't render that page.

| Area | Evidence (integration tests on real PostgreSQL, fake providers) |
|---|---|
| Flag off, pre-feature database | `PreFeatureMigrationTests`: legacy jobs and deliverables survive the upgrade, read through the current model, get no vertical selection, and the finished legacy vertical video becomes a one-video `LegacySingle` library. No feature rows appear; no flag row exists, so the flag is off. |
| Flag off, database with feature data | `SwitchingOffWithFeatureDataKeepsEverythingStartsNothingAndReEnablingDuplicatesNothing`: creative testing, performance and rule services refuse; the finished library video still downloads; the render regression suite starts and cancels; worker ticks start nothing; every feature row count is unchanged. |
| Legacy campaign path when off | `LibrarySwitchedOffMeansTheLegacyCampaignAndTheGlobalSwitchBeatsThePilotList`, `LibrarySwitchedOffWhileQueuedRunsTheLegacyCampaign` |
| Feature-only routes and content hidden | `TheArticleHeadlineAndLinksFollowTheSwitch` (article 404, sitemap, home copy, links), bUnit tests for the Campaign tab planner, review gallery and admin tab |
| Kill switch during an active library run | `LibrarySwitchedOffBetweenVideosKeepsTheFinishedOneStartsNoOtherAndReEnablingAddsNothing` |
| Kill switch with queued creative test cases | `KillSwitchStopsQueuedCreativeCasesFromStartingAndLeavesTheRegressionSuiteUsable` |
| Re-enable is idempotent | both tests above, plus the rollout test: no new library, variant, deliverable, usage record or provider call |
| One usage record per campaign | `LibraryOnRendersEachRecommendedFormatAsItsOwnVideoAndBillsTheCampaignOnce`, `ThreeVideoDefaultCostsOneUsageRecordAndItsProviderCallsAreBounded` |
| Four listing profiles | `AcceptanceProfilesGetTheFormatsTheirVerifiedInputsSupportAndNothingElse` (below) |
| Captions, narration, safe zones | `VerticalCaptionRenderingTests`, `VerticalLibraryRenderingTests` (FFmpeg end-to-end, ffprobe checks) |
| Downloads, review decisions, revisions | `VerticalLibraryReviewTests` |
| Admin suites | `VerticalCreativeTestTests`, `AdminCampaignReviewPageTests` (render regression suite) |
| Metric entry | `VerticalPerformanceTests` |
| Tenant isolation | every service test above has a second workspace that sees and changes nothing; the acceptance test repeats it across the four profiles |
| Unsupported claims and unconsented presenters | `FormatRecommendationReadsOnlyTheWorkspacesStoryboardAndConsentedPresenter`, `ConfirmedClaimsUnlockStoryAndAccessFormatsUntilWithdrawnOrExpired`, the acceptance test's banned-claim check on every hook |
| Existing campaigns, presets, old URLs | `CampaignSettingsPresetTests` (presets saved before the library are unchanged byte for byte), `CampaignGenerationTests`, `CampaignReviewTests`, `PropertyDetailsRenderingTests` |

**Four listing profiles** (`AcceptanceProfilesGetTheFormatsTheirVerifiedInputsSupportAndNothingElse`):
- **Standard listing** (five ordinary storyboard photos): Wow Feature and Price First, each with reasons; Hosted POV, Story Reveal and Exclusive Access ineligible, each saying what is missing.
- **Luxury / visually distinctive** (exceptional pool and exterior): Wow Feature ranks first and scores higher than the standard listing's.
- **Agent-hosted footage** (40 s processed walk-through with sound): Hosted POV becomes eligible only once the agent confirms the footage as a hosted walk-through.
- **Verified renovation story**: Story Reveal is ineligible until the agent confirms the problem and payoff and marks before and after photos. Then the hook is the agent's own words, the story opens on the before photo, and no other format uses the before photo.
- Every profile: 1 to 3 recommendations, each with reasons; recipes only for eligible formats; no hook outside Story Reveal contains renovation, exclusivity, price-change, seller, neighborhood or affordability language; no owner can plan, tag or confirm on another owner's listing.

**Measured cost of the three-video default** (fake providers, so wall time measures the app and database only, not real AI or rendering time):
| Measure | Value |
|---|---|
| Videos recommended and rendered (standard fixture: 5 photos) | 2 (Wow Feature, Price First; a third needs hosted footage or a confirmed story) |
| Failed videos | 0 |
| Render calls | 2 (one per video, no re-renders) |
| Voice calls | 2 (master voiceover plus Price First; Wow Feature is music only) |
| Story and editorial plan calls | 1 each, shared by every video |
| Usage records | 1 |
| Worker stage attempts for the whole campaign | 25 (each stage ran once; no retries) |
| Wall time with fake providers | under 2 s |
| Stored bytes | 12 (fake renderer placeholder files, so not a real size) |

What this means for production:
- **Provider calls** per campaign: one story, one editorial plan, one master voiceover, plus one voiceover per narrated video (Wow Feature is music only), and one render per video. With the default of three recommendations, that is up to 3 renders and up to 3 voiceovers instead of 1 and 1.
- **Storage** grows by one MP4 per video (about 3x for a three-video library). Each is stored under its own campaign/library/video/version path.
- **Real generation time** is roughly one extra render and voiceover per extra video. It has not been measured against real providers, because no paid provider may be called here.

### Operational thresholds (proposed for the pilot)
Turn the global switch off (immediate rollback) if any of these hold over a pilot day:
- more than 10% of library videos end `Failed` (render or technical validation)
- any campaign has more than one usage record
- any rendered video fails the ffprobe/decode validator in production
- worker stage retries for library stages exceed 3 per campaign on average
- storage growth above 4x the pre-pilot daily average
- any report of a claim the agent did not confirm, or a presenter used without consent

### Exact feature-flag instructions
1. Sign in as a platform admin and open **Admin → Release switches** (`/admin/features`).
2. Leave **Every workspace** off. Press **Add tester and admin workspaces**, check the list, and add any other pilot workspace with the search box.
3. Turn the **Global switch (kill switch)** on, read the summary ("On for N pilot workspaces."), and press **Save**. The change is seen by every host within 15 seconds.
4. To widen later, add workspaces or turn on **Every workspace**. To stop, turn the **Global switch** off and Save: that overrides the pilot list and the every-workspace switch, which are kept for when it is turned back on.

### Immediate rollback checklist
1. `/admin/features`: turn the **Global switch (kill switch)** off, Save. Confirm the summary says "Off for everyone".
2. Wait 15 seconds (cache lifetime), then check:
   - the Campaign tab shows the single shared vertical video option again
   - the TikTok Creative format testing tab and the article are gone
   - `/articles/one-listing-four-tiktoks` returns 404
3. Queues:
   - campaigns already queued run the legacy single-video path
   - a library campaign between videos keeps the videos it finished and starts no new one
   - queued creative test cases do not start; the render regression suite still works
4. Leave all additive data in place. Nothing is deleted, and it all comes back if the switch is turned on again, without duplicates or a second charge.

### Permanent revert checklist (only with owner approval)
1. Do the immediate rollback first and wait until no library campaign is `Queued` or `Running`.
2. **Schema tolerance check (done for this PR):** every feature migration is additive. New columns on existing tables are nullable or have a default (`CampaignDeliverables.CampaignVideoVariantId`, `CampaignGenerationJobs.VerticalSelection`, `VerticalFormats`, `VerticalGoal`, `VerticalHookVariant`). The rebuilt unique index on `CampaignDeliverables (OrganizationId, CampaignGenerationJobId, Kind)` still applies to every row the old app writes (`CampaignVideoVariantId IS NULL`). So the previous app can run against the new schema.
3. **One data check the previous app needs:** it doesn't know the five new campaign stage names. A finished library campaign ends at `FinalizeCampaign`, which the old app knows, but a failed or cancelled one can be left at a library stage. Run:
   ```sql
   SELECT count(*) FROM "CampaignGenerationJobs"
   WHERE "CurrentStage" IN ('PlanVerticalLibrary','GenerateVariantNarration','RenderVariants','GenerateVariantCopy','FinalizeVerticalLibrary');
   ```
   If it isn't 0, the owner decides whether to move those terminal rows to `FinalizeCampaign` before reverting, or keep the feature code. The old app would fail to read those rows.
4. Also expect, in the previous app:
   - library campaigns show each library video as an extra vertical deliverable
   - saved settings keep a `verticalSelection` value, which the old app ignores
5. Revert the feature PR's merge commit on main and run **Deploy production**. Do not run the migrations' Down methods (development only).
6. Data retention: the owner decides whether to keep the feature tables or approve a later cleanup migration. Until then they are unused. The tables are `CampaignVideoLibraries`, `CampaignVideoVariants`, the claim/role tables, creative tests, posts, metric snapshots and rule reports.

### Queued-work behavior
| Situation | Behavior |
|---|---|
| Flag turned off with a library campaign queued | runs the legacy single-video campaign |
| Flag turned off between library videos | finished videos stay; no new video starts; the campaign reaches its existing safe terminal state |
| Flag turned off with creative test cases queued | they do not start; finished cases and their evidence stay |
| Flag turned back on | nothing restarts or duplicates on its own; a repeat request returns the same campaign; no second usage record |
| Worker retry | variant renders use idempotency keys, so a retried stage reuses the existing video |

### Additive migration notes
- Six migrations: `AddVerticalVideoLibrary`, `AddVerticalMediaRolesAndClaims`, `AddVerticalLibraryGeneration`, `AddVerticalCampaignGoal`, `AddVerticalCreativeTests`, `AddVerticalPerformanceMetrics`.
- `AddVerticalVideoLibrary` back-fills each finished legacy vertical video into a one-video `LegacySingle` library. The landscape tour and unfinished jobs are untouched.
- `AddVerticalPerformanceMetrics` adds a trigger that makes metric snapshots append-only.
- The production migration job applies them in order; tested from the last pre-feature migration with legacy data and on an empty database.

### Data retention requirements
- Metric snapshots are append-only by design: correcting a number means adding a newer reading. Deleting them needs a migration that drops the trigger, which is an owner decision.
- Posts store only the platform link without query strings. Reports store counts and rates with pseudonymous agent labels.
- Article clicks store slug and placement only.
- Nothing stores narration text, secrets or signed URLs in logs.

### Support notes
- "I don't see the new vertical videos": check the workspace is on the pilot list at `/admin/features` (or the every-workspace switch), and that the global switch is on.
- "Hosted POV or Story Reveal isn't offered": the agent has to confirm host footage, presenter consent, or the renovation story and before/after photos. The Campaign tab lists what is missing for each format.
- "I was charged more than once": one campaign makes one usage record regardless of video count. Check `CampaignUsageRecords` for the job.
- Release switch changes are in the admin audit log (`platform-admin.features.configure`).

### Dependency audit, startup, migrations
- `dotnet list ListingStudio.slnx package --vulnerable --include-transitive`: no vulnerable packages in any project. The feature adds one package, `bunit` 2.13.3, to the unit test project only.
- **Startup.** A fresh local PostgreSQL 16 database was migrated with `dotnet ef database update`, which applied every migration. Then the Web and Worker started in Development, with `Voice__Provider=OpenAI` (no provider was called). Both logged "Application started" with no fail or crit entries:
  - The Worker started every processor, including the campaign generation and creative test workers.
  - The Web served `/` and `/articles` with 200 and the sitemap with 200.
  - `/admin/features` redirected anonymous visitors to sign in (302).
  - `/articles/one-listing-four-tiktoks` returned 404, because the flag is off by default.
- **Migrations:** from the representative pre-feature database and on an empty database, both passed (`PreFeatureMigrationTests`). There are no pending model changes.
- **Rollback analysis:** done by reading the migrations and the previous app's model, not by running the previous app (see the permanent revert checklist).

Screenshots of the release page (desktop and phone): project files `reviews/2026-11-01-vertical-prompt-12/`.

### Unresolved defects and remaining risks
- Nothing here was run against real OpenAI, ElevenLabs, HeyGen or Higgsfield, so real generation time, provider failure rates and cost per three-video campaign are unmeasured. Measure them in the pilot.
- The 60-second full-voiceover production failure from Prompt 2 was not reproduced locally; its cause still needs the production worker error.
- During a permanent revert, failed or cancelled library campaigns left at a library stage need the data check above.
- The acceptance profiles test recommendations and plans for all four listings. Full generation, review and metrics are covered by the per-area tests on the standard listing fixture, not repeated for each profile.

### Flag, schema, queue and rollback (this prompt)
- **Flag off:** the release page itself is always available to platform admins; everything else is as described above.
- **Schema:** none new in Prompt 12.
- **Queued work:** none.
- **Rollback:** revert this commit; the switch can still be changed from the database or by the existing `IFeatureStateService`.

### Files
- Application: `Administration/IFeatureRolloutService.cs`
- Infrastructure: `Administration/FeatureRolloutService.cs`, DI registration
- Web: `Pages/AdminFeatures.razor` (+ css), `Pages/AdminDashboard.razor`
- Tests:
  - `UnitTests/AdminFeaturesTests.cs`
  - `IntegrationTests/VerticalRolloutTests.cs`
  - `IntegrationTests/PreFeatureMigrationTests.cs`
  - `IntegrationTests/VerticalAcceptanceTests.cs`
  - `IntegrationTests/PostgreSqlWebApplicationFixture.cs` (connection string)

## The decision the owner needs to make
Choose one:
1. **Start the pilot:** merge PR #123 and run Deploy production. The six migrations run; the flag stays off. Then turn the flag on for tester and admin workspaces at `/admin/features`.
2. **Hold:** keep the PR open as a draft for more review. Nothing changes in production.

Either way, no cleanup migration is needed now; feature data retention is decided only if the feature is later removed.
