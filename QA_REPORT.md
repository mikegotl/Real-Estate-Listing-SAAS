# Listing Studio pilot QA report

Date: 2026-10-05

Scope: Week 16 quality assurance and pilot hardening

Target: `codex/week-16-quality-assurance-pilot-hardening`

## Executive summary

The repository builds cleanly and the complete automated suite passes with 74 unit tests and 38 PostgreSQL integration tests. The critical campaign journey is covered with deterministic provider fakes and real persistence: account registration and login, property creation, photo upload, idempotent campaign generation, durable monitoring after a new scope (the server-side equivalent of refresh/reconnect), organization-scoped inline preview/download, and HERO, FEATURE, and TEASER output retrieval.

No Critical code finding remains. This hardening pass corrected worker termination after transient infrastructure failures, unbounded upload reads and image dimensions, missing inline result previews, and missing campaign-level provider failure coverage.

Production pilot activation remains gated on the owner decisions and credentials listed under **Pilot deployment gates**. No paid provider was contacted and no production resource was deployed during this review.

## Automated journey and failure coverage

| Requirement | Evidence | Result |
| --- | --- | --- |
| Register and organization ownership | `AuthenticationTests.RegistrationCreatesUserOrganizationAndOwnerMembership` | Pass |
| Login | `AuthenticationTests.RegisteredCredentialsCanLogIn` | Pass |
| Create/manage property | `PropertyManagementTests.OwnerCanCreateReadUpdateAndArchiveProperty` | Pass |
| Upload and manage photos | `PropertyMediaManagementTests.OwnerCanUploadFortyReorderReadAndDeletePhotos` | Pass |
| Generate, monitor, refresh, and retrieve campaign | `CampaignGenerationTests.UploadToGenerateWaitAndDownloadIsDurableIdempotentAndTenantScoped` | Pass |
| Preview HERO/FEATURE/TEASER | Authenticated inline range endpoint plus the campaign page's three HTML video players; asset authorization is exercised by the campaign tenant test | Pass; browser playback still requires the owner acceptance check |
| Download HERO/FEATURE/TEASER | All `CampaignOutputKind` values are opened, byte-verified, and denied cross-tenant in the full campaign test | Pass |
| Invalid/oversized/deceptive image | `UploadValidatesTypeContentAndLimit` and `UploadRejectsOversizedAndDeceptiveStreamsWithoutReadingPastDeclaredSize` | Pass |
| AI timeout and invalid structured response | `StoryTimeoutIsRetriedAndReportedWithoutProviderDetails`; OpenAI director schema failure tests | Pass |
| Voice provider failure | `VoiceFailureIsRetriedAndStopsAtNarrationCheckpoint` | Pass |
| AI-video timeout | `AiVideoTimeoutIsRetriedAndStopsBeforeRendering` | Pass |
| FFmpeg failure | `FailedRenderRetriesOnlyItsCheckpointAndCanBeManuallyResumed`; renderer diagnostic and cleanup tests | Pass |
| Database/infrastructure failure | `BackgroundWorkerResilienceTests` for both hosted workers | Pass |
| Duplicate Generate | concurrent enqueue in the full campaign test returns one job and one usage record | Pass |
| Browser refresh/reconnect | campaign state is reloaded from PostgreSQL through fresh dependency-injection scopes throughout the full campaign test | Pass at server boundary |
| Worker restart | durable checkpoints and expired leases are reclaimable; hosted-worker regression tests prove polling survives an infrastructure exception | Pass at worker boundary |
| Expired authentication | security-stamp revalidation and anonymous route redirects use ASP.NET Core Identity; password-reset test proves old credentials stop working | Framework/integration coverage; add browser automation after pilot UX stabilizes |
| Unauthorized organization | property, media, story, plan, narration, campaign, generated clip, billing, and download tests | Pass |
| Duplicate Stripe webhook | signed replay in `BillingTests.TestCustomerCanCheckoutOpenPortalAndUpdateBillingFromVerifiedIdempotentWebhooks` | Pass |

## Findings

### Critical

None found.

### High — corrected

1. **Hosted workers could terminate after a transient database/infrastructure exception**
   - Location: `src/ListingStudio.Worker/Worker.cs`, `src/ListingStudio.Worker/CampaignGenerationWorker.cs`
   - Problem: exceptions outside the domain processors escaped `BackgroundService.ExecuteAsync`.
   - Consequence: the default host behavior could stop the Worker process, leaving all queued work stalled until an external restart.
   - Correction: each polling iteration now logs the failure, preserves cancellation semantics, waits the configured interval, and retries. Regression tests make each processor fail once and prove a later iteration succeeds.

2. **Upload streams could consume more memory than their declared size**
   - Location: `src/ListingStudio.Infrastructure/Properties/PropertyMediaService.cs`
   - Problem: `CopyToAsync` read the complete stream before comparing its actual and declared lengths; image dimensions were not capped.
   - Consequence: a malicious or faulty client could make the server buffer excessive data or pass a decompression-bomb-sized image to downstream tooling.
   - Correction: copying stops after the declared length plus one byte, the existing 20 MiB limit is enforced before reading, and images are rejected above 15,000 pixels on either side or 50 million total pixels.

### Medium — corrected

1. **Completed deliverables had no in-product preview**
   - Location: `src/ListingStudio.Web/Components/Properties/CampaignGenerationManager.razor`, `src/ListingStudio.Web/Program.cs`
   - Problem: the page exposed attachment downloads only.
   - Consequence: agents could not review a campaign before downloading it.
   - Correction: the page now renders an accessible HTML video player for every completed deliverable. The authenticated endpoint serves inline content by default and an attachment only for `?download=true`; both paths keep range processing and tenant checks.

2. **Provider failure coverage stopped short of the full campaign orchestrator**
   - Location: `tests/ListingStudio.IntegrationTests/CampaignGenerationTests.cs`
   - Problem: lower-level adapters were tested, but AI timeout, voice failure, and AI-video timeout were not proven through durable campaign retries/checkpoints.
   - Consequence: a regression could repeat completed expensive stages, render after a failed prerequisite, or expose provider messages to users.
   - Correction: integration tests now prove three retries, checkpoint isolation, render suppression, and sanitized persisted errors.

### Medium — open recommendations

1. **Production password reset has no configured email adapter**
   - Location: `src/ListingStudio.Web/Components/Account/DevelopmentIdentityEmailSender.cs`
   - Problem: non-development reset delivery deliberately throws because no production email provider was selected.
   - Consequence: a pilot user who forgets a password cannot self-recover the account.
   - Recommended correction: select a transactional-email provider, implement it behind `IEmailSender<ApplicationUser>`, store its credential in Key Vault, and verify delivery and token expiry in the production-like environment.

2. **Registration and paid-provider spend policy requires an owner decision**
   - Location: `src/ListingStudio.Web/Components/Account/Pages/Register.razor`, `src/ListingStudio.Infrastructure/Campaigns/CampaignGenerationService.cs`, `src/ListingStudio.Infrastructure/Billing/BillingService.cs`
   - Problem: registration is public and campaign usage is recorded as additional usage rather than blocked when an allowance is exhausted.
   - Consequence: an Internet-facing pilot with paid AI credentials could acquire unapproved users or provider cost; whether overage should be allowed, charged, capped, or invite-only is a product decision.
   - Recommended correction: before public exposure, choose invite-only versus public registration and define a hard spend/overage policy. Keep optional AI-video disabled until that policy and provider budget alerts exist.

3. **Browser automation does not yet cover the interactive Blazor journey**
   - Location: test solution
   - Problem: critical behavior is covered at domain, service, HTTP authorization, persistence, renderer, and component-markup boundaries, but not through Playwright.
   - Consequence: client-side selectors, reconnect behavior, and visual playback controls could regress without failing CI.
   - Recommended correction: add a small Playwright smoke suite after the pilot UI stabilizes; retain the existing service tests as the authoritative data-integrity suite.

4. **Privacy retention and deletion policy is not encoded**
   - Location: property/media lifecycle and operations documentation
   - Problem: users can archive properties and delete individual media, but the product has no organization-level export/deletion workflow or documented retention schedule.
   - Consequence: customer offboarding and privacy requests require manual operator action.
   - Recommended correction: define the pilot data-retention agreement and a tested support runbook before accepting customer data.

### Low

1. **Interactive authentication revalidation interval is 30 minutes**
   - Location: `src/ListingStudio.Web/Components/Account/IdentityRevalidatingAuthenticationStateProvider.cs`
   - Problem: an already-connected Blazor circuit can take up to 30 minutes to observe a security-stamp change.
   - Consequence: password reset or administrator invalidation is not immediate in an existing circuit.
   - Recommended correction: consider a shorter interval for the pilot if the additional database traffic is acceptable, and cover the selected behavior in browser automation.

## Security and architecture review notes

- Domain remains dependency-free; provider-specific types remain in adapters.
- Business reads and mutations reviewed in this pass derive organization identity from the authenticated user and include organization predicates. Cross-organization tests cover every primary aggregate and generated asset path.
- Stripe webhook requests require a valid signature, enforce a bounded timestamp tolerance and body size, store provider event IDs for replay protection, and reject stale subscription events.
- Provider response bodies and credentials are not persisted or included in user-visible errors. Production secrets remain runtime configuration/Key Vault concerns.
- FFmpeg is invoked without a shell, is time-bounded, captures diagnostics, and removes partial output on failure.
- `dotnet list ListingStudio.slnx package --vulnerable --include-transitive` found no known vulnerable packages from the configured NuGet source.

## Verification evidence

Executed locally on 2026-10-05:

```text
dotnet build ListingStudio.slnx --configuration Release
  Build succeeded. 0 warnings, 0 errors.

dotnet test ListingStudio.slnx --configuration Release --no-build
  Unit:       74 passed, 0 failed, 0 skipped.
  Integration: 38 passed, 0 failed, 0 skipped.

dotnet list ListingStudio.slnx package --vulnerable --include-transitive
  No vulnerable packages found in any project.
```

Integration tests used disposable PostgreSQL 17 Testcontainers. Paid OpenAI, voice, AI-video, Stripe, and Azure services were replaced with test doubles or local emulators; those results are not evidence that real provider credentials or production delivery work.

## Pilot deployment gates

Do not open the application to pilot customers until the owner has:

1. selected and configured a production password-reset email provider, or explicitly accepted a documented manual recovery process;
2. chosen invite-only/public registration and hard-cap/overage behavior for paid provider usage;
3. configured only approved provider credentials in Key Vault and verified budget alerts;
4. approved the data-retention/offboarding runbook;
5. completed the browser acceptance check for register, login, create property, photo upload, generate, refresh/reconnect, inline preview, and all three downloads.
