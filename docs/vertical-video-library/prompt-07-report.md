## Prompt 7: recommendation-led vertical workflow on the Campaign tab

**When it shows.** Only when `VerticalVideoLibraryV1` is on for the workspace (kill switch included), the property is not archived, and the vertical output is ticked. Otherwise the Campaign tab is unchanged: same controls, copy, validation, routes and submission. A new deep link, `/properties/{id}?tab=campaign&step=goal|formats`, scrolls to that step. With the switch off it falls back to the normal Campaign tab (tested).

**Step 1: goal.** Maximize reach, Generate buyer interest, Promote an open house, Announce a new listing, Announce a price change.
- Stored as `VerticalCampaignGoal`, separate from the format and the purpose.
- A goal pre-selects the matching campaign message (Fast social tour, Feature spotlight, Open house, Just listed, Price drop). The agent can still change it under Creative direction.
- The goal never picks a format. The open house and price change goals still ask for the confirmed time or amount.

**Step 2: videos this listing can support.** One card per format. Each card shows:
- score and strength (Recommended badge, Strong, Good, Weak, Not ready)
- purpose, reasons, missing inputs (or "would score higher with"), example hook, length (and loop) and the opening shot

Actions:
- "Use the recommended N videos" and "Make every eligible video (N)".
- A "Make this video" checkbox per card.
- "Swap for…" on each picked video.

Ineligible formats can't be ticked. The request records RecommendedThree or AllEligible when the pick matches those sets, and the new `AgentChosen` selection with the agent's list otherwise.

**Unlock more videos** (collapsed panel). The agent can:
- confirm a story problem and result, or an access line with an optional end date
- withdraw either one
- tick property videos that show them hosting

All of these use the Prompt 5 append-only services, in the agent's own words. The scores recalculate after each change.

**Campaign-wide controls stay.** Brand, language, voice, caption look, music, agent card and presenter consent are unchanged. Under Customize: the style and voiceover used only if the campaign falls back to one shared vertical video. Each format's timing and captions are fixed so every line stays tied to a verified fact.

**Before you generate.**
- The primary button reads "Generate high-potential vertical videos".
- An estimate reads "You'll get 3 vertical videos and 1 other file" (or "1 shared vertical video" when nothing is eligible).
- It explains that the campaign counts once against the monthly allowance however many videos it makes, and that one failed video doesn't stop the others.
- If every eligible video is unticked, the button is disabled with "Pick at least one video".
- If Wow feature is picked with No music, the agent is warned that it will be silent.

**Copy.** The library view no longer says videos open with price and location, or that one file is posted to all three platforms. It says each video has its own hook and each 9:16 file can go to TikTok, Reels or Shorts. The legacy view keeps its accurate copy.

**Request and validation (service).**
- New `EnqueueAsync(…, VerticalLibraryRequest)` and `IsVerticalLibraryEnabledAsync`.
- The request shape is validated: 1 to 5 distinct library formats, only with AgentChosen.
- When the switch is on, agent-chosen formats must be eligible at request time. Wow feature and Price first are let through while photo analysis is still running. All formats are checked again, and dropped if still ineligible, when the library is planned.
- The fingerprint adds the goal and the picked formats, so the same request finds the same campaign.
- The result now carries `VerticalRequest` (selection, goal, formats), which the running campaign shows as a one-line summary.

**Accessibility and layout.** Cards are list items labelled by their headings. Score chips have spoken labels, and the estimate is an `aria-live` region. Goals are a radio group, and every control is a native checkbox, radio or button (keyboard reachable). Opening photos have alt text. One column below 600 px.

### Flag, schema, queue and rollback
- **Flag off:** legacy Campaign tab and legacy request, byte for byte. Library requests are ignored; goal and formats are not stored (tested).
- **Schema:** additive migration `AddVerticalCampaignGoal`: nullable `CampaignGenerationJobs.VerticalGoal` (string 32) and `VerticalFormats` (string 128). `VerticalVideoSelection.AgentChosen` is appended (stored by name). Down is development only.
- **Queued work:** unchanged. An agent-chosen job planned after the listing changed drops formats that are no longer eligible. If none are left, it falls back to the shared vertical video (Prompt 6 path).
- **Rollback:** switch off. Old code ignores the two new columns. An `AgentChosen` value would not parse in old code, so the Prompt 12 revert step must clear the selection on unfinished jobs.

### Commands run
- `dotnet build ListingStudio.slnx`: 0 warnings. `migrations has-pending-model-changes`: none.
- `RUN_FFMPEG_E2E=1 dotnet test ListingStudio.slnx`: 572 unit and 233 integration tests passed. A reason wording change was made after this run, and the unit tests were run again (572 passed).
- New bUnit component tests (`VerticalCampaignPlannerTests`, 6), using bUnit 2.13.3, added to the unit test project:
  - every format's score, strength, reasons, hook, length and opening shot
  - ineligible formats explain what's missing and can't be picked
  - remove, swap, make all and use recommended
  - an empty pick and the goal callback
  - loading and no-media fallbacks
  - screen-reader labels
- New unit tests (`VerticalCampaignWorkflowTests`, 6): the picker rules, request validation, the job storing goal and formats, and the goal-to-message mapping.
- New integration tests:
  - `AgentPickedVideosAreTheOnlyOnesMadeAndTheGoalIsKept`: renders the agent's one pick, keeps the goal, and finds the same campaign on a repeat request.
  - `PickingAVideoTheListingCannotSupportIsRejectedUnlessTheLibraryIsOff`
  - `CampaignTabShowsTheLegacyControlsUntilTheVerticalLibraryIsOnAndDeepLinksFallBack`: server-rendered Campaign tab, switch off and then on.
- Screenshots of the new steps (desktop and phone): project files `reviews/2026-11-01-vertical-prompt-7/`.

### Remaining risks
- Screenshots come from the component rendered with the app theme, not from a full signed-in browser session.
- The recommendation is worked out when the tab opens, and again when the goal or an unlock input changes. Changing presenter consent or the price reduction elsewhere on the tab doesn't refresh the scores until the tab reloads.
- While photo analysis is pending, photo-led formats can't be picked in the UI (they aren't eligible yet). The default request still asks for the recommended set, which is worked out again at planning.
- Saved campaign settings don't store the goal or an agent-chosen list. Only the recommended/all choice is stored (from Prompt 3).
- Library videos still aren't shown as separate cards in the results; that is Prompt 8.
