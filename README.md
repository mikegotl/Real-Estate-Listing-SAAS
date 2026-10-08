# Listing Studio

.NET 10 foundation for a real-estate Listing Studio, implemented as a clean modular monolith with a Blazor Web App, an ASP.NET Core background worker, PostgreSQL persistence, organization tenancy, ASP.NET Core Identity, and a MudBlazor property-management workspace.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (the SDK is pinned in `global.json`)
- Docker with Docker Compose v2 (for PostgreSQL)
- FFmpeg and ffprobe 6 or newer with `vidstabdetect`, `vidstabtransform`, `deflicker`, `grayworld`, and `normalize` filters (for deterministic rendering and uploaded-video enhancement)

## Projects

| Project | Responsibility |
| --- | --- |
| `ListingStudio.Domain` | Framework-independent domain core |
| `ListingStudio.Application` | Use cases and abstraction contracts |
| `ListingStudio.Infrastructure` | PostgreSQL, Identity, Blob Storage, and Stripe adapters |
| `ListingStudio.AI` | OpenAI and voice/TTS adapters |
| `ListingStudio.Video` | Deterministic FFmpeg rendering and future AI-video adapters |
| `ListingStudio.Web` | Blazor/ASP.NET Core web composition root |
| `ListingStudio.Worker` | Background processing composition root |
| `ListingStudio.UnitTests` | Fast tests for inward layers |
| `ListingStudio.IntegrationTests` | Tests of the composed web host |

See [ARCHITECTURE.md](ARCHITECTURE.md) for dependency rules.

## Configure local development

Tracked JSON files contain empty placeholders only. Copy the environment template and replace its example values locally:

```bash
cp .env.example .env
```

Set `POSTGRES_PASSWORD` and the password segment of `PostgreSQL__ConnectionString` to the same local-only value. The connection string is quoted because semicolons have special meaning in a shell. Load the local variables before running .NET commands:

```bash
set -a
source .env
set +a
```

Never commit `.env`, user secrets, access keys, or connection strings. ASP.NET Core reads environment variables with `__` as the section separator. Alternatively, keep development credentials in .NET user secrets:

```bash
dotnet user-secrets init --project src/ListingStudio.Web
dotnet user-secrets set --project src/ListingStudio.Web "OpenAI:ApiKey" "your-key"
dotnet user-secrets set --project src/ListingStudio.Web "OpenAI:Model" "gpt-6-luna"
dotnet user-secrets set --project src/ListingStudio.Worker "OpenAI:ApiKey" "your-key"
dotnet user-secrets set --project src/ListingStudio.Worker "OpenAI:Model" "gpt-6-luna"
dotnet user-secrets set --project src/ListingStudio.Worker "MediaAnalysis:Enabled" "true"
```

The same runtime configuration sections are available to both hosts: `PostgreSQL`, `AzureBlobStorage`, `AddressLookup`, `NeighborhoodInsights`, `OpenAI`, `AIVideo`, `Voice`, `FFmpeg`, `VideoBranding`, `CampaignGeneration`, and `Stripe`. The Web host also supports the `Authentication:Google` and `Authentication:Apple` sections described below.

### Google and Apple sign-in

External sign-in is optional and disabled until a provider is fully configured. Local email/password registration remains available. A new Google or Apple user authenticates with the provider, confirms an organization name, and becomes that organization's owner. For security, an external identity is never automatically linked to an existing Listing Studio account solely because the email addresses match.

For Google, create a Web OAuth client, register the development callback URL `https://localhost:{PORT}/signin-google` and the corresponding production HTTPS callback URL, then store the credentials outside source control:

```bash
dotnet user-secrets set --project src/ListingStudio.Web "Authentication:Google:ClientId" "your-client-id"
dotnet user-secrets set --project src/ListingStudio.Web "Authentication:Google:ClientSecret" "your-client-secret"
dotnet user-secrets set --project src/ListingStudio.Web "Authentication:Google:Enabled" "true"
```

For Apple web sign-in, an Apple Developer membership and a Sign in with Apple-enabled App ID are required. Create a Services ID, register the exact HTTPS return URL `https://your-host/signin-apple`, and create a Sign in with Apple private key. Configure the Services ID as `ClientId`, along with the Apple Developer `TeamId`, key `KeyId`, and the complete PKCS #8 `.p8` contents:

```bash
dotnet user-secrets set --project src/ListingStudio.Web "Authentication:Apple:ClientId" "your-services-id"
dotnet user-secrets set --project src/ListingStudio.Web "Authentication:Apple:TeamId" "your-team-id"
dotnet user-secrets set --project src/ListingStudio.Web "Authentication:Apple:KeyId" "your-key-id"
dotnet user-secrets set --project src/ListingStudio.Web "Authentication:Apple:PrivateKey" "$(< /secure/path/AuthKey_KEYID.p8)"
dotnet user-secrets set --project src/ListingStudio.Web "Authentication:Apple:Enabled" "true"
```

Apple requires a registered HTTPS website return URL, so plain loopback HTTP isn't a valid live Apple test setup. The app generates Apple's rotating client-secret JWT at runtime from the private key; the key must come from user secrets locally and a managed secret store in production. Provider buttons appear only when their provider is enabled, and startup fails clearly when an enabled provider is missing required settings.

Listing photos use the provider selected by `AzureBlobStorage:Provider`. The default `Local` provider writes ignored development files beneath `App_Data/property-media`; set the provider to `Azure` and supply `AzureBlobStorage:ConnectionString` at runtime to use the configured Blob container. Do not put the Azure connection string in tracked settings.

Property details also accept up to 10 MP4, M4V, MOV, or WEBM clips, each up to 500 MB and five minutes. The upload is container-probed before persistence, remains private and organization-scoped, and is retained unchanged. A separate Worker queue creates a web-compatible H.264/AAC MP4 with two-pass motion stabilization, constant 30fps cadence, flicker reduction, gray-world white balance, luminance/color normalization, and conservative contrast/saturation correction. The page shows queued, processing, completed, and failed states; failed jobs can be explicitly retried, while completed clips can be previewed with range requests and downloaded. `VideoProcessing:Enabled` and `VideoProcessing:PollIntervalSeconds` control the Worker, and `FFmpeg:EnhancementTimeoutSeconds` bounds each FFmpeg pass.

Production uses `AzureBlobStorage:ServiceUri` plus the Container App managed identity instead of a storage connection string. The identity receives only `Storage Blob Data Contributor`, shared-key access is disabled, and the container remains private.

Property-photo analysis runs only in the Worker and is disabled by default so local startup cannot accidentally spend API credits. To enable it, provide `OpenAI__ApiKey` and a vision-capable `OpenAI__Model`, then set `MediaAnalysis__Enabled=true`. The Worker processes one photo at a time, waits the configured polling interval between requests, and retries transient failures up to three times. Automated tests always replace the provider with a fake and never call OpenAI.

Property marketing stories are generated on demand after every uploaded image has completed analysis. Story generation uses the same runtime-only OpenAI key and model, requests strict structured output, and validates the result against verified property facts before saving it. Identical verified inputs reuse the existing version to avoid another provider request. The initial branding input is the organization name; explicit agent profile branding is deferred to BrandKit. Automated tests use a fake story generator and never spend API credits.

Video production plans can be generated after a grounded property story exists. The OpenAI video director returns editorial choices only; server code supplies and validates property/story identity, media IDs, exact fact bindings, BrandKit values, output profile, safe zone, and CTA before storing an immutable specification. Stored story claims must still pass grounding against current verified facts; regenerate the story if edits invalidate its claims. Invalid structured responses, schema drift, and requests to change property features are rejected. Concurrent identical requests reuse a single provider call. Plans support 60, 30, and 15 seconds in landscape or vertical format. Identical verified inputs reuse the stored plan, and changed inputs produce the next version. BrandKit editing and licensed-asset management will be exposed by the later campaign workflow; renderer callers already supply resolved, authorized asset files by logical ID. Automated tests replace the director with a fake and never call paid services.

Narration generation defaults to OpenAI text to speech and uses the existing `OpenAI__ApiKey`; no separate voice-provider credential is needed. The defaults are `gpt-4o-mini-tts`, the `marin` voice, and a warm real-estate narration instruction. The provider requests documented 24 kHz, 16-bit little-endian PCM and wraps it in a standards-compliant WAV container locally. Set `Voice__Provider=OpenAI` explicitly when overriding configuration. OpenAI narration is treated as one continuous audio track across the planned narration window because the speech endpoint does not return segment timing metadata.

An owner can optionally upload one private TXT, Markdown, or DOCX narration script from the property page. Uploading stores and previews the extracted text but does not use it in a video. To authorize its use, check **I confirm this script is accurate and approved for real-estate marketing**. Accepted scripts must contain 5–145 words so the exact text fits the 60-second master. Replacing a document clears acceptance and requires a fresh review. Generate or refresh the campaign after accepting or revoking: existing videos never change in place. The 60-second master narrates every accepted segment verbatim and in order; 30- and 15-second edits use exact excerpts rather than AI-written substitutes.

ElevenLabs remains available as an alternative by setting `Voice__Provider=ElevenLabs`, `Voice__ApiKey`, and `Voice__VoiceId`; its default model is `eleven_multilingual_v2` and default format is `mp3_44100_128`. Local development stores ignored assets beneath `App_Data/campaign-assets`; the Azure provider uses the configured private Blob container. Identical narration input reuses the existing asset, while a changed provider, voice, model, output, or instruction configuration creates a new immutable version. The campaign UI discloses that narration uses an AI voice. Automated tests always use a fake provider.

The deterministic renderer is configured through `FFmpeg:ExecutablePath`, `FFmpeg:ProbeExecutablePath`, `FFmpeg:RenderTimeoutSeconds`, and the `VideoBranding` template section. It accepts typed still-image, narration, logo, and licensed-music assets and produces a 1920x1080 landscape or 1080x1920 vertical, 30fps H.264/AAC MP4. It supports exact scale/crop, Ken Burns motion, cuts, crossfades, dip-to-black transitions, grounded opening titles/property facts/lower thirds/closing CTAs, agent and brokerage logos, configurable whole-video fades, measured narration, background-music fades, and planned narration ducking. Brand colors and contact values come from the specification's BrandKit; font sizes, padding, opacity, color roles, and program fades come from configuration rather than hard-coded campaign values. Set `VideoBranding:FontFilePath` to an installed TrueType/OpenType font when the FFmpeg environment cannot resolve the default `Sans` family. Arguments are passed directly to FFmpeg without a shell. Rendering fails rather than overwriting an existing output, captures process diagnostics and duration, honors cancellation, and removes partial output after failure or timeout.

## Run

Start PostgreSQL and restore the repository-local EF Core tool:

```bash
docker compose up -d postgres
docker compose ps
dotnet tool restore
```

Apply the database migration:

```bash
dotnet ef database update \
  --project src/ListingStudio.Infrastructure \
  --startup-project src/ListingStudio.Web
```

Restore, build, and test the full solution:

```bash
dotnet restore ListingStudio.slnx
dotnet build ListingStudio.slnx --no-restore
dotnet test ListingStudio.slnx --no-build
```

Run the Web host and, when background analysis is needed, the Worker in separate terminals:

```bash
dotnet run --project src/ListingStudio.Web
dotnet run --project src/ListingStudio.Worker
```

Open the Web URL printed by `dotnet run`. Registering creates an Identity user, a new organization, and an owner membership. `/auth` is protected and redirects anonymous users to `/Account/Login`. In Development only, the forgot-password confirmation page displays the locally generated reset link. Configure a production identity email adapter before deploying; reset tokens are never written to logs or source control.

After signing in, open `/properties` to create, list, inspect, edit, and archive properties. The new-property form queries the server-side ArcGIS World Geocoding adapter after four typed characters; selecting a U.S. address fills street, city, state, and ZIP while manual entry remains available. Configure this through `AddressLookup`, or set `AddressLookup__Enabled=false` to disable outbound suggestions. No geocoding credential is sent to the browser.

Neighborhood Insights is optional and disabled by default to prevent accidental Google Places charges. Enable Places API (New) in a billing-enabled Google Cloud project, restrict the key to that API and the Web host's server addresses, then store it outside source control with `NeighborhoodInsights__ApiKey` and set `NeighborhoodInsights__Enabled=true`. The property page can then retrieve nearby schools, parks, libraries, groceries, healthcare, dining, shopping and transit. Results show straight-line distance, source links, current provider photos and attribution. A signed-in owner must approve each fact before it enters the next generated story or campaign. School proximity never asserts attendance assignment or quality, and generated copy rejects family-suitability, safety, demographic, rating and school-boundary claims. Stored place facts are tenant-scoped; Google photo references are fetched on demand for in-app review and are never cached or exported.

To include neighborhood imagery in downloadable campaign videos, approve the place and upload a separate JPG, PNG, or WEBP image that you own or are licensed to use. Enter the required photo credit before upload. The private, tenant-scoped asset is timed to the matching grounded neighborhood fact and its credit is burned into every generated MP4 that uses it. Refreshing Google place results preserves the uploaded asset when the provider place ID remains present. If an approved place has no licensed video photo, the campaign still uses its factual words but keeps the property photo as the visual background.

A property's details page accepts up to 50 JPG, JPEG, PNG, or WEBP originals of at most 20 MB each. It displays thumbnails and metadata, reports upload progress, retries failed selections, and supports drag-and-drop or accessible arrow-button reordering. Archived properties and their photos are read-only and hidden from the default dashboard; select **Show archived** and refresh to include them. Property and media queries derive organization scope from the authenticated user's membership, and never accept an organization identifier from the browser.

When media analysis is enabled, the Worker claims pending photos with a PostgreSQL work lease, stores structured category, room type, quality, hero suitability, visibility flags, potential problems, factual description, and suggested ordering, and schedules bounded retries. Completed results and failed-attempt state appear on the property details page; an owner can explicitly requeue a failed analysis.

After all property photos show completed analysis, use **Generate story** on the property details page to create campaign title, hook, narrative, highlights, voiceover, closing call to action, and long and short social captions. Generated output is immutable and versioned. Changing verified inputs produces a new version; unchanged inputs return the stored story without a new AI request. A configured provider credential is required for this manual workflow.

Use **Generate Campaign** on the property details page to enqueue the complete background workflow. The Web request returns immediately; the Worker validates the listing, analyzes pending media, generates or reuses the grounded story, master plan, narration and derivatives, renders HERO 60, FEATURE 30 and TEASER 15 landscape MP4s, stores social copy, and finalizes the campaign. When a narration script has marketing acceptance, it replaces generated voiceover in newly created or refreshed videos. Direction aligns its passages with the most relevant analyzed photos and may use bounded windows from completed, stabilized property walkthroughs for passages about layout, flow, movement, or a general tour. Walkthrough source audio is not mixed into the deliverable. The page polls durable status every two seconds and shows every checkpoint plus retry, cancellation, organization-scoped inline previews, and explicit downloads.

Campaign generation is enabled by default. `CampaignGeneration:PollIntervalSeconds` controls Worker polling, `StageTimeoutSeconds` bounds one checkpoint, and `LeaseSeconds` controls interrupted-work recovery. Start both Web and Worker for this workflow. A checkpoint is committed before the next starts, automatic failures receive at most three attempts, and an explicit retry resumes at the failed checkpoint without repeating completed expensive provider calls. Unexpected polling/database failures are logged and retried rather than terminating the Worker. An unchanged property/media input reuses its completed campaign. Live OpenAI checkpoints require `OpenAI__ApiKey`; ElevenLabs additionally requires its runtime credentials only when selected. Automated integration tests replace every paid provider and the renderer with deterministic fakes.

Uploads are capped at 20 MiB, 15,000 pixels on either side, and 50 million pixels total. The server reads no more than one byte beyond the client-declared length when detecting a size mismatch.

Selective image-to-video generation is disabled by default. When `AIVideo:Enabled=true`, configure `Provider`, `ApiKey`, `Endpoint`, `Model`, and `GenerationVersion` at runtime. The endpoint must be an HTTPS synchronous multipart gateway accepting `image`, `prompt`, `duration_seconds`, `aspect_ratio`, and `model`, and honoring the supplied `Idempotency-Key` header. It returns an MP4 body plus `X-Video-Width`, `X-Video-Height`, and `X-Video-Duration-Ms` headers, and may also return `X-Provider-Request-Id`, `X-Provider-Model`, and `X-Estimated-Cost-Usd`. Set `RequestTimeoutSeconds` and `MaxOutputMegabytes` to bound the call. Only production-plan scenes explicitly marked for generative motion invoke the gateway. Identical media/instruction/duration/aspect/version input reuses the private cached clip and its recorded provider, model, request, metadata, and estimated cost. If the feature is disabled, marked scenes render from their original property-photo fallback with no provider call.

### Stripe test-mode billing

Stripe billing is disabled by default and no automated test contacts Stripe. To exercise it manually, create two recurring **test-mode** prices in Stripe, then provide `Stripe__SecretKey`, `Stripe__StarterPriceId`, `Stripe__ProfessionalPriceId`, and the corresponding monthly campaign allowances through `.env` or user secrets. Set `Stripe__PublicBaseUrl` to the externally reachable Web origin used for Stripe return URLs, keep `Stripe__ApiBaseUrl=https://api.stripe.com`, set `Stripe__Enabled=true`, and never place live or test credentials in tracked files.

Forward Stripe test events to the exact webhook endpoint and copy the CLI-provided signing secret into runtime configuration:

```bash
stripe login
stripe listen --forward-to https://localhost:5001/billing/stripe-webhook
dotnet user-secrets set --project src/ListingStudio.Web "Stripe:WebhookSecret" "your-cli-signing-secret"
```

Start the Web host, register and sign in, then open `/billing`. Choosing a plan creates a server-side Stripe Customer and Checkout Session; an existing customer can open Stripe's Customer Portal. Use Stripe test card `4242 4242 4242 4242`, any future expiry and any CVC only on Stripe-hosted test checkout. Listing Studio never receives or stores card data. The checkout return page is informational: subscription plan, status and period change only after a valid signed `checkout.session.completed` or `customer.subscription.*` webhook. Webhook event IDs are stored for replay protection, older events cannot replace newer subscription state, and campaign usage is counted once per newly created generation job.

For a fully local webhook check after checkout, trigger a test update and inspect `/billing` after Stripe delivers it:

```bash
stripe trigger customer.subscription.updated
```

Stripe CLI fixtures may not reference the customer created by your browser checkout. The authoritative end-to-end check is therefore the Dashboard/CLI event for that test customer: confirm a 2xx delivery to `/billing/stripe-webhook`, then verify the plan, status, current period and usage shown in `/billing`. Do not enable live mode or create production resources as part of this test.

CI installs FFmpeg plus a deterministic font, renders a fully branded sample with narration and music, then generates and renders HERO, FEATURE, and TEASER landscape deliverables plus a vertical teaser from one master. One derivative uses a locally generated motion clip through the same typed input used by cached AI-video assets. Every output is probed for its exact duration, dimensions, and codecs. To opt into those tests locally after installing FFmpeg and ffprobe, set `RUN_FFMPEG_E2E=1` before running the integration test suite.

Integration tests require a running Docker daemon. They start a disposable PostgreSQL 17 container and apply the committed migrations automatically.

Stop PostgreSQL with `docker compose down`; add `--volumes` only when you also intend to delete local database data.

## Production deployment

The repository contains separate production containers for Web, Worker and the EF migration bundle, plus Bicep for Azure Container Apps, PostgreSQL Flexible Server, Blob Storage, ACR, Application Insights, Log Analytics, Key Vault, managed identity and private database networking. Production hosts expose `/health/live` and `/health/ready`; readiness checks PostgreSQL and the container-installed FFmpeg binary. Production logging is structured JSON and is exported through Azure Monitor OpenTelemetry when `APPLICATIONINSIGHTS_CONNECTION_STRING` is configured.

Deployment is intentionally manual and approval-gated through the GitHub `production` environment. It uses OIDC rather than a stored Azure client secret, pushes only immutable commit-SHA images, runs migrations as a one-off Container Apps Job, and enables the Worker only after migration succeeds. No Azure resources are created by normal CI or by merging a pull request. See [DEPLOYMENT.md](DEPLOYMENT.md) for setup, deployment, secret configuration, verification and rollback.
