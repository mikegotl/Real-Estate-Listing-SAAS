# Listing Studio build plan

Source: *Listing Studio 16 Week AI Assisted Build Playbook*, supplied by the project owner. The prompts below preserve its weekly requirements; this file supplies repository state for unattended work. The source estimates 48 three-hour founder sessions over 16 weeks. Calendar weeks are a planning target, not evidence that a milestone passed.

## Product and architecture boundaries

- Paid-pilot MVP: verified property facts plus 20-50 photos produce HERO 60, FEATURE 30, TEASER 15, social captions and marketing copy.
- Base pipeline: property -> photos -> AI analysis -> story -> production specification -> narration -> deterministic FFmpeg render -> derivatives -> delivery. Optional generative video must not block this path.
- Maintain the existing .NET 10 modular monolith. Codex implements bounded slices; Claude reviews architecture, schemas, prompts and high-risk changes. The owner accepts product decisions and real workflow checks.

## State and progression

- `pending`: not started or not independently verified. `proposed`: work exists in an open PR. `accepted`: the owner reviewed and merged a passing PR. A successful automation run does not change milestone status by itself.
- Week 1 was accepted on 2026-10-03 after PR #4 merged with passing restore/build/tests, successful Blazor and Worker startup checks, and owner-confirmed Docker Compose/PostgreSQL validation.
- Week 2 was accepted on 2026-10-03 after owner review of PR #6, passing local and GitHub Actions verification, a successful PostgreSQL migration, and manual authentication-flow acceptance.
- Week 3 was accepted on 2026-10-03 after owner-directed merge of PR #7, passing local and GitHub Actions verification, successful PostgreSQL migration, tenant-isolation tests, and manual property-management acceptance.
- Week 4 was accepted on 2026-10-03 after the owner merged PR #8 with passing local and GitHub Actions verification, a successful PostgreSQL migration, 40-image automated acceptance, and manual upload, thumbnail, and reorder checks.
- Week 5 was accepted on 2026-10-03 after PR #9 merged with passing build/tests/migration/provider-contract verification; the owner explicitly deferred the paid live OpenAI vision check to a later integration stage.
- Week 6 was accepted on 2026-10-04 after the owner merged PR #10 with passing local and GitHub Actions verification, grounded fake-provider generation, immutable versioning, cache reuse, and tenant-isolation checks.
- Work on only the first `pending` milestone whose prerequisites are accepted. If an open PR already covers it, review or repair that PR instead of opening a duplicate. Do not begin the next milestone until the prior PR is merged and accepted.
- Record acceptance evidence in the PR: commands, results, manual checks, remaining blockers. Never claim an unperformed test passed. Keep the `main` branch protected from unattended merges or deployments.

## Ordered milestones

### Week 1: Architecture and Repository

**Status:** accepted (2026-10-03; PR #4 merged and Docker/PostgreSQL acceptance confirmed)

**Expected result:** A compiling modular-monolith solution that runs locally with Docker and includes documented architecture and test projects.

**Acceptance:** git clone -> docker compose up -> dotnet run works.

**Claude Prompt (from playbook)**

```text
You are acting as a senior .NET SaaS architect.

I am building a SaaS called Listing Studio for real-estate agents.

The application takes a property listing plus approximately 20-50 property photographs and automatically produces a
branded real-estate marketing campaign.

The eventual outputs are:
- HERO video - approximately 60 seconds
- FEATURE video - approximately 30 seconds
- TEASER video - approximately 15 seconds
- Social captions
- Property marketing copy

Technology constraints:
- .NET 10
- Blazor Web App
- ASP.NET Core
- C#
- Entity Framework Core
- PostgreSQL
- Azure Blob Storage
- FFmpeg
- OpenAI API
- AI image-to-video provider
- ElevenLabs or equivalent TTS
- Hangfire/background worker
- Stripe
- Azure
- Docker
- GitHub Actions

I want a modular monolith for the MVP, NOT microservices.

AI providers, voice providers, storage providers and video-generation providers must be abstracted behind interfaces
so they can be replaced later.

Design the solution architecture.

Identify:
1. Projects
2. Responsibilities
3. Domain entities
4. Interfaces
5. Dependency direction
6. Database boundaries
7. Background-job architecture
8. Media pipeline
9. Video-rendering architecture
10. AI-provider architecture
11. Error/retry strategy
12. Testing strategy

Keep the architecture appropriate for an MVP that could later scale commercially.

Do not generate implementation code yet.
```

**Codex Prompt (from playbook)**

```text
Implement the initial Listing Studio solution architecture described in ARCHITECTURE.md.

Requirements:
- .NET 10
- Blazor Web App
- ASP.NET Core
- C#
- Clean modular-monolith architecture

Create:
- ListingStudio.Web
- ListingStudio.Application
- ListingStudio.Domain
- ListingStudio.Infrastructure
- ListingStudio.AI
- ListingStudio.Video
- ListingStudio.Worker

Tests:
- ListingStudio.UnitTests
- ListingStudio.IntegrationTests

Add appropriate project references while enforcing dependency direction.
Add dependency injection registration.
Add Docker Compose with PostgreSQL.
Add configuration placeholders for PostgreSQL, Azure Blob Storage, OpenAI, AI Video, Voice/TTS and Stripe.
Do NOT put secrets into source control.
Create README.md with instructions for running the solution.
Build the entire solution and run all tests before considering the task complete.
Do not implement business features yet.

Report:
- files created
- architectural decisions
- commands executed
- build result
- test result
- remaining issues
```

### Week 2: Database and Authentication

**Status:** accepted (2026-10-03; PR #6 owner-reviewed and approved for merge)

**Expected result:** Users can register, sign in, sign out and access protected pages. Organization membership establishes tenant isolation.

**Acceptance:** Create an account, sign in and reach authenticated pages backed by PostgreSQL.

**Codex Prompt (from playbook)**

```text
Implement authentication and the initial persistence layer for Listing Studio.

Use the existing architecture. Do not restructure the solution.

Implement:
- ApplicationUser
- Organization
- OrganizationMember

Configure PostgreSQL using EF Core.
Use ASP.NET Core Identity.

Users must be able to:
- Register
- Login
- Logout
- Reset password
- Access authenticated pages

Every business entity will eventually belong to an Organization.
Design the schema for multi-tenant data isolation now, even though the MVP will initially have simple accounts.
Create EF Core migrations.
Add validation and appropriate indexes.

Create integration tests proving:
- User registration works
- Login works
- Unauthenticated users cannot access protected resources
- Organization membership works

Run migrations, build the solution and execute all tests.
Do not implement properties yet.
```

### Week 3: Property Management

**Status:** accepted (2026-10-03; PR #7 owner-directed and merged after passing acceptance)

**Expected result:** A usable property dashboard with create, edit, archive, list and detail workflows.

**Acceptance:** A signed-in user can manage properties, and cross-organization access tests pass.

**Codex Prompt (from playbook)**

```text
Implement Property Management as a complete vertical slice.

A logged-in real-estate agent must be able to:
- View properties
- Create property
- Edit property
- Archive property
- Open property details

Property fields:
- Address1
- Address2
- City
- State
- ZipCode
- ListingPrice
- Bedrooms
- Bathrooms
- SquareFeet
- LotSize
- YearBuilt
- PropertyType
- Description
- ListingStatus

Every property belongs to an Organization.
Use MudBlazor for the UI.

Implement:
- Domain model
- EF configuration
- Migration
- Application services
- Validation
- Blazor pages/components
- Authorization
- Unit tests
- Integration tests

Users must never be able to access another organization's properties.
Do not implement media uploads yet.
Build and test everything.
```

### Week 4: Listing Photo Management

**Status:** accepted (2026-10-03; PR #8 merged after passing acceptance)

**Expected result:** Agents can upload, preview, reorder, retry and delete as many as 50 listing images.

**Acceptance:** Upload 40 listing photographs and manage them in Blazor.

**Codex Prompt (from playbook)**

```text
Implement Property Media Upload.

Requirements:
- Allow JPG, JPEG, PNG and WEBP.
- Maximum 50 images per property.
- Validate MIME type and file size.
- Store original media in Azure Blob Storage.
- Store metadata in PostgreSQL.

PropertyMedia should include:
- Id
- PropertyId
- BlobPath
- OriginalFilename
- MimeType
- FileSize
- Width
- Height
- DisplayOrder
- UploadedAt
- AnalysisStatus

Implement:
- Multiple-file upload
- Upload progress
- Thumbnail display
- Drag/reorder support
- Delete
- Retry failed upload

Design IPropertyMediaStorage so Azure Blob Storage is replaceable.
Provide a local-development storage implementation if necessary.
Do not implement AI analysis.
Add tests.
Build and run all tests.
```

### Week 5: AI Photo Understanding

**Status:** accepted (2026-10-03; PR #9 merged, automated/provider-adapter verification accepted; live paid OpenAI vision check deferred)

**Expected result:** The system classifies each property photo, scores its quality and hero suitability, and stores the result asynchronously.

**Acceptance:** A background analysis run produces validated, persisted results for every uploaded image.

**Codex Prompt (from playbook)**

```text
Implement Property Media Analysis.

Create IPropertyMediaAnalyzer and an OpenAI-backed implementation.

For every property photograph, return structured data containing:
- Category
- RoomType
- QualityScore
- HeroScore
- IsExterior
- IsInterior
- ContainsPeople
- PotentialProblems
- Description
- SuggestedDisplayOrder

Possible categories include FrontExterior, RearExterior, Aerial, Entry, LivingRoom, Kitchen, DiningRoom,
PrimaryBedroom, Bedroom, PrimaryBathroom, Bathroom, Office, Laundry, Garage, Pool, Patio, Backyard, Community
and Other.

Persist the analysis.
Analysis must be asynchronous and retryable.
Do not tightly couple the domain/application layer to OpenAI.
Use structured output rather than parsing prose.
Add tests using a fake IPropertyMediaAnalyzer.
Do not call the real API from automated tests.


Runtime AI Prompt
You are analyzing professional real-estate listing photography.

Classify the supplied photograph.

Determine:
- room/property category
- brief factual visual description
- image quality from 0-100
- hero-image suitability from 0-100
- whether the image is interior or exterior
- whether people are visible
- potential visual problems
- recommended position within a property-tour sequence

Do not infer property features that are not clearly visible.

Do not infer:
- location
- price
- school district

- neighborhood quality
- materials unless visually certain
- room dimensions
- property condition beyond visible evidence

Return only the requested structured schema.
```

### Week 6: Property Marketing Story

**Status:** accepted (2026-10-04; PR #10 owner-merged with passing automated acceptance)

**Expected result:** Verified listing facts and visible media observations become a grounded narrative, voiceover and social copy.

**Acceptance:** Generate a stored campaign story that passes fact-grounding validation.

**Codex Prompt (from playbook)**

```text
Implement IPropertyStoryGenerator.

Input must consist of:
- Verified Property data
- PropertyMediaAnalysis records
- Agent/organization branding

Output a structured PropertyStory containing:
- CampaignTitle
- OpeningHook
- PropertyNarrative
- Highlights
- VoiceoverScript
- ClosingCTA
- SocialCaptionLong
- SocialCaptionShort

CRITICAL RULE:
The model must never introduce a property fact that isn't contained in verified Property data.

AI may transform verified facts into marketing language but may not manufacture square footage, room counts,
upgrades, materials, views, amenities, school information, neighborhood claims, location claims, HOA information or
financial claims.

Implement structured output.
Persist the generated story and retain the generation version.
Add unit/integration tests using fake providers.


Runtime AI Prompt
Act as a real-estate marketing copywriter.

Create compelling marketing language using ONLY the supplied verified property facts and media observations.

Media observations describe visible imagery but are not authoritative property facts.
Never introduce a factual property claim unless supported by VERIFIED_PROPERTY_DATA.
Create a natural visual progression through the property.
The voiceover should sound conversational and cinematic rather than like an MLS field list.
Avoid excessive adjectives and cliches.
Return the specified structured JSON only.
```

### Week 7: AI Video Director

**Status:** implementation proposed (design approved and merged in PR #11; owner acceptance required)

**Expected result:** A versioned, machine-readable production specification contains every editorial decision the renderer needs.

**Acceptance:** A validated 60-second production specification can be generated and stored without renderer-side AI calls.

**Claude Prompt (from playbook)**

```text
Design a machine-readable Video Production Specification for Listing Studio.

It needs to describe a real-estate promotional video sufficiently for a deterministic C#/FFmpeg rendering engine to
execute it.

Support:
- 60/30/15 second videos
- scenes
- listing images
- AI-generated clips
- scene duration
- transitions
- Ken Burns movement
- text overlays
- voiceover segments
- music
- logo
- property information
- CTA
- aspect ratio
- safe zones

A scene should reference PropertyMedia IDs rather than filenames.
Design a JSON schema that can be strongly typed using C# records/classes.
AI should make editorial decisions.
FFmpeg should make rendering decisions.
Clearly define that boundary.

Return:
- schema
- sample 60-second project
- validation rules
- C# model recommendations
- versioning strategy
```

**Codex Prompt (from playbook)**

```text
Implement the Video Production Specification approved in VIDEO_SPEC.md.

Create IVideoDirector.
Implement an OpenAI VideoDirector.

Input:
- Property
- PropertyStory
- PropertyMediaAnalysis
- BrandKit
- RequestedDuration
- AspectRatio

Output:
- VideoProductionSpecification

The specification must contain everything required by the rendering engine without requiring the renderer to call AI.
Validate every media ID.
Validate total duration.
Prevent unsupported property claims.
Persist specifications.
Implement specification versioning.
Create comprehensive unit tests.


Runtime AI Prompt
You are the director/editor of a premium real-estate property tour.

Using the verified property information, property story and classified listing media, create a video production plan.

Prioritize the strongest imagery.
Establish the property first.
Then create a logical visual tour.
Avoid excessive reuse of photographs.
Match narration to visible imagery.
Use subtle cinematic motion.
Reserve important property facts for appropriate scenes.
End with a clear CTA.
The total scene duration must equal the requested video duration.
Reference only supplied media IDs.
Do not invent property facts.
Return only the VideoProductionSpecification schema.
```

### Week 8: Narration and Audio

**Status:** pending

**Expected result:** The system turns production-spec narration into stored audio with duration and timing metadata.

**Acceptance:** Checkpoint: Property -> photos -> AI analysis -> story -> scene plan -> narration works reliably.

**Codex Prompt (from playbook)**

```text
Implement narration generation.

Create:
- IVoiceProvider
- VoiceGenerationRequest
- VoiceGenerationResult

Implement ElevenLabsVoiceProvider.

Input:
- VideoProductionSpecification narration

Output:
- audio file
- duration
- timing metadata where available

Store generated audio using IPropertyMediaStorage or an appropriate campaign storage abstraction.
Do not expose ElevenLabs types outside Infrastructure.
Support cancellation, retry and API errors.
Add fake provider for testing.
Never call ElevenLabs during automated tests.
```

### Week 9: FFmpeg Rendering Engine

**Status:** pending

**Expected result:** The first automatically rendered 1080p property tour combines still-image motion, transitions and narration.

**Acceptance:** The sample campaign renders end to end as a playable MP4.

**Codex Prompt (from playbook)**

```text
Implement the first Listing Studio FFmpeg rendering engine.

Create:
- IVideoRenderer
- FfmpegVideoRenderer

Input:
- VideoProductionSpecification
- Property media
- Narration audio

Output:
- MP4

Initially support:
- 1920x1080
- 30fps
- H.264
- AAC
- still-image scenes
- scale/crop
- Ken Burns pan/zoom
- crossfade
- narration

FFmpeg commands must be generated from strongly typed models rather than arbitrary strings throughout the
application.
Capture stdout, stderr, exit code and render duration.
Return useful errors when rendering fails.
Create automated tests for command generation.
Create one end-to-end rendering test using sample media.
Do not add advanced branding yet.
```

### Week 10: Professional Branding and Audio Mix

**Status:** pending

**Expected result:** Campaign videos include consistent titles, listing data, agent branding, music, ducking and a closing call to action.

**Acceptance:** A complete sample looks client-ready and uses data-driven branding.

**Codex Prompt (from playbook)**

```text
Extend the FFmpeg renderer with Listing Studio branding.

Add:
- Property address title
- Price
- Beds/Baths/Square Feet where supplied
- Agent name
- Agent logo
- Brokerage logo
- Lower thirds
- Opening title
- Closing CTA
- Background music
- Music ducking under narration
- Fade in/out
- Configurable transitions

Create BrandKit:
- Logo
- SecondaryLogo
- AgentName
- Phone
- Email
- Website
- PrimaryColor
- SecondaryColor

Branding must be template-driven rather than hard-coded.
Do not change the VideoProductionSpecification architecture unless necessary.
Create a sample campaign and render it end-to-end.
```

### Week 11: Campaign Derivatives

**Status:** pending

**Expected result:** One master campaign produces HERO 60, FEATURE 30 and TEASER 15 versions in landscape and vertical formats.

**Acceptance:** One master campaign reliably creates all three deliverables without unnecessary AI regeneration.

**Codex Prompt (from playbook)**

```text
Implement Campaign Derivative Generation.

One master campaign must produce:
- HERO 60 - approximately 60 seconds
- FEATURE 30 - approximately 30 seconds
- TEASER 15 - approximately 15 seconds

The shorter versions should reuse the strongest media/story decisions from the master campaign rather than
independently starting over.

Implement ICampaignDerivativeGenerator.
Generate separate VideoProductionSpecifications.

Support:
- 16:9
- 9:16

Ensure important subjects/text remain inside safe zones.
Render all three videos.
Add automated duration validation.
Do not regenerate expensive AI assets unnecessarily.
```

### Week 12: One Click Campaign Automation

**Status:** pending

**Expected result:** Generate Campaign starts a durable background workflow that can resume, retry and report progress.

**Acceptance:** Upload -> Generate Campaign -> wait -> download works. This is the first fully demoable product milestone.

**Codex Prompt (from playbook)**

```text
Implement asynchronous Campaign Generation.

User action: Generate Campaign must enqueue a background workflow rather than perform generation during the
HTTP request.

Stages:
1. ValidateProperty
2. AnalyzeMedia
3. GenerateStory
4. GenerateMasterVideoPlan
5. GenerateNarration
6. GenerateDerivativePlans
7. GenerateRequiredAiVideo
8. RenderHero
9. RenderFeature
10. RenderTeaser
11. GenerateSocialCopy
12. FinalizeCampaign

Track each stage.
Expose progress to Blazor.

Support:
- retry
- failure
- cancellation
- idempotency
- worker restart
- API timeout

A failed stage must not require restarting completed expensive stages.
Create CampaignGenerationJob and appropriate persistence.
Add integration tests.
```

### Week 13: Selective Generative Video

**Status:** pending

**Expected result:** Selected scenes can use subtle AI motion while the normal deterministic photo pipeline remains fully functional.

**Acceptance:** An explicitly selected scene can use a cached AI-generated clip without weakening the standard render path.

**Codex Prompt (from playbook)**

```text
Implement optional image-to-video generation.

Create IAiVideoProvider.

Input:
- PropertyMedia
- motion instruction
- duration
- aspect ratio

Output:
- GeneratedVideoClip

AI-generated video must be optional.
The normal FFmpeg photo-animation pipeline must continue working without it.
AI clips should only be generated for scenes explicitly marked as requiring generative motion.
Cache generated clips.
Never regenerate an identical clip unnecessarily.
Track generation cost and provider metadata.
Provide fake implementation for tests.
Do not allow provider-specific types outside Infrastructure.


Runtime AI Prompt
Create subtle cinematic camera movement from this real-estate listing photograph.

Preserve the architecture, furniture, windows, doors, fixtures, landscaping and spatial layout exactly as shown.
Do not add, remove or redesign property features.

Movement:
slow cinematic push forward

Maintain realistic real-estate photography.
No people.
No text.
No architectural hallucinations.
```

### Week 14: Subscriptions and Usage Billing

**Status:** pending

**Expected result:** Listing Studio supports plans, monthly allowances, additional usage, customer self-service and reliable billing state.

**Acceptance:** A test customer can subscribe, reach the portal and have verified webhooks update server-side billing state.

**Codex Prompt (from playbook)**

```text
Implement SaaS billing using Stripe.

Architecture must support:
- subscription plan
- monthly campaign allowance
- additional usage
- usage tracking
- billing status

Implement:
- Stripe Checkout
- Stripe Customer
- Subscription
- Webhook handling
- Customer Portal

Persist:
- StripeCustomerId
- SubscriptionId
- Plan
- SubscriptionStatus
- CurrentPeriod
- CampaignUsage

Webhook processing must be idempotent.
Never trust client-side payment state.
Do not store payment card information.
Create test-mode integration documentation.
```

### Week 15: Azure Production Deployment

**Status:** pending

**Expected result:** The application and worker deploy through CI/CD with secure configuration, health checks, logging and rollback instructions.

**Acceptance:** A clean commit can build, test and deploy the public application and healthy worker through GitHub Actions.

**Codex Prompt (from playbook)**

```text
Prepare Listing Studio for production deployment to Azure.

Production components:
- Blazor/ASP.NET application
- .NET background worker
- PostgreSQL
- Azure Blob Storage
- Application Insights
- Key Vault

Containerize appropriate applications.
Create GitHub Actions CI/CD.

Pipeline must:
- restore
- build
- test
- publish
- containerize
- deploy

Secrets must come from secure configuration.

Configure:
- production logging
- health checks
- structured logging
- environment configuration
- database migrations
- worker health
- FFmpeg availability

Create DEPLOYMENT.md containing complete deployment and rollback instructions.
Do not commit secrets.
```

### Week 16: Quality Assurance and Pilot Hardening

**Status:** pending

**Expected result:** The full customer journey and major failure paths are tested, reviewed and corrected before pilot use.

**Acceptance:** All pilot-blocking findings are fixed and the complete test suite passes.

**Claude Prompt (from playbook)**

```text
Act as a senior .NET SaaS engineer performing a pre-production code review.

This application is about to be used by real pilot customers.

Review the Listing Studio repository.

Focus on:
- security
- tenant isolation
- authentication
- authorization
- secrets
- data integrity
- race conditions
- background-job reliability
- AI failure handling
- FFmpeg process handling
- resource cleanup
- large-file handling
- API cost controls
- Stripe webhook security
- logging
- privacy
- test coverage
- architectural violations

Do not rewrite the application.

Rank findings by:
- Critical
- High
- Medium
- Low

For every finding provide:
- file/location
- problem
- real-world consequence
- recommended correction

Also identify anything that should prevent pilot deployment.
```

**Codex Prompt (from playbook)**

```text
Act as the senior QA engineer for Listing Studio.

Review the entire repository.

Create automated tests for the critical customer journey:

- Register
- Login
- Create property
- Upload listing photos
- Generate campaign
- Monitor processing
- Preview results
- Download HERO
- Download FEATURE
- Download TEASER

Use xUnit, integration tests and Playwright where appropriate.

Test failure conditions including:
- invalid image
- oversized image
- AI timeout
- AI invalid structured response
- voice provider failure
- video provider timeout
- FFmpeg failure
- database failure
- duplicate Generate click
- browser refresh
- worker restart
- expired authentication
- unauthorized organization access
- Stripe webhook duplication

Do not hide failures by weakening assertions.
Run the complete test suite.
Produce QA_REPORT.md documenting failures and recommendations.
```
