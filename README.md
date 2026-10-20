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

The same runtime configuration sections are available to both hosts: `PostgreSQL`, `AzureBlobStorage`, `AddressLookup`, `NeighborhoodInsights`, `OpenAI`, `AIVideo`, `Higgsfield`, `PhotoFixes`, `Voice`, `FFmpeg`, `VideoBranding`, `CampaignGeneration`, and `Stripe`. The Web host also supports the `Authentication:Google`, `Authentication:Apple`, and production `DataProtection` sections described below.

### Google and Apple sign-in

External sign-in is optional and disabled until a provider is fully configured. Local email/password registration remains available. A new Google or Apple user authenticates with the provider, confirms an organization name, and becomes that organization's owner. For security, an external identity is never automatically linked to an existing Listing Studio account solely because the email addresses match.

For Google, create a Web OAuth client, register the development callback URL `https://localhost:{PORT}/signin-google` and the corresponding production HTTPS callback URL, then store the credentials outside source control:

```bash
dotnet user-secrets set --project src/ListingStudio.Web "Authentication:Google:ClientId" "your-client-id"
dotnet user-secrets set --project src/ListingStudio.Web "Authentication:Google:ClientSecret" "your-client-secret"
dotnet user-secrets set --project src/ListingStudio.Web "Authentication:Google:Enabled" "true"
```

Google blocks OAuth inside some embedded and in-app browsers. On iPhone or iPad, open Listing Studio in Safari or Chrome before choosing **Continue with Google**.

For Apple web sign-in, an Apple Developer membership and a Sign in with Apple-enabled App ID are required. Create a Services ID, register the exact HTTPS return URL `https://your-host/signin-apple`, and create a Sign in with Apple private key. Configure the Services ID as `ClientId`, along with the Apple Developer `TeamId`, key `KeyId`, and the complete PKCS #8 `.p8` contents:

```bash
dotnet user-secrets set --project src/ListingStudio.Web "Authentication:Apple:ClientId" "your-services-id"
dotnet user-secrets set --project src/ListingStudio.Web "Authentication:Apple:TeamId" "your-team-id"
dotnet user-secrets set --project src/ListingStudio.Web "Authentication:Apple:KeyId" "your-key-id"
dotnet user-secrets set --project src/ListingStudio.Web "Authentication:Apple:PrivateKey" "$(< /secure/path/AuthKey_KEYID.p8)"
dotnet user-secrets set --project src/ListingStudio.Web "Authentication:Apple:Enabled" "true"
```

Apple requires a registered HTTPS website return URL, so plain loopback HTTP isn't a valid live Apple test setup. The app generates Apple's rotating client-secret JWT at runtime from the private key; the key must come from user secrets locally and a managed secret store in production. Provider buttons appear only when their provider is enabled, and startup fails clearly when an enabled provider is missing required settings.

Listing photos use the provider selected by `AzureBlobStorage:Provider`. The default `Local` provider writes ignored development files beneath the repository-level `App_Data/property-media` folder, which the Web and Worker hosts share (each host's `LocalRootPath` is `../../App_Data/property-media`, resolved from its project folder); set the provider to `Azure` and supply `AzureBlobStorage:ConnectionString` at runtime to use the configured Blob container. Do not put the Azure connection string in tracked settings.

Property details also accept up to 10 MP4, M4V, MOV, or WEBM clips, each up to 500 MB and five minutes. The upload is container-probed before persistence, remains private and organization-scoped, and is retained unchanged. A separate Worker queue creates a web-compatible H.264/AAC MP4 with two-pass motion stabilization, constant 30fps cadence, flicker reduction, gray-world white balance, luminance/color normalization, and conservative contrast/saturation correction. The page shows queued, processing, completed, and failed states; failed jobs can be explicitly retried, while completed clips can be previewed with range requests and downloaded. `VideoProcessing:Enabled` and `VideoProcessing:PollIntervalSeconds` control the Worker, and `FFmpeg:EnhancementTimeoutSeconds` bounds each FFmpeg pass.

Production uses `AzureBlobStorage:ServiceUri` plus the Container App managed identity instead of a storage connection string. The identity receives only `Storage Blob Data Contributor`, shared-key access is disabled, and the container remains private.

The production Web host stores its ASP.NET Core Data Protection key ring in the private `listingstudio-data-protection` Blob container and encrypts that ring with the versionless `data-protection` Key Vault key. The shared managed identity receives `Storage Blob Data Contributor` and `Key Vault Crypto User`; `DataProtection:BlobUri` and `DataProtection:KeyVaultKeyIdentifier` are injected by Bicep. This keeps authentication, OAuth correlation, and antiforgery cookies readable across revisions and replicas without adding a secret.

Property-photo analysis runs only in the Worker and is disabled by default so local startup cannot accidentally spend API credits. To enable it, provide `OpenAI__ApiKey` and a vision-capable `OpenAI__Model`, then set `MediaAnalysis__Enabled=true`. Set the same switch on the Web host too: it shows Find room photos on listing videos only when room recognition is enabled. The Worker processes one photo at a time, waits the configured polling interval between requests, and retries transient failures up to three times. Automated tests always replace the provider with a fake and never call OpenAI.

Property marketing stories are generated on demand after every uploaded image has completed analysis. Story generation uses the same runtime-only OpenAI key and model, requests strict structured output, and validates the result against verified property facts before saving it. Identical verified inputs reuse the existing version to avoid another provider request. The initial branding input is the organization name; explicit agent profile branding is deferred to BrandKit. Automated tests use a fake story generator and never spend API credits.

Video production plans can be generated after a grounded property story exists. The OpenAI video director returns editorial choices only; server code supplies and validates property/story identity, media IDs, exact fact bindings, BrandKit values, output profile, safe zone, and CTA before storing an immutable specification. Stored story claims must still pass grounding against current verified facts; regenerate the story if edits invalidate its claims. Invalid structured responses, schema drift, and requests to change property features are rejected. Concurrent identical requests reuse a single provider call. Plans support 60, 30, and 15 seconds in landscape or vertical format. Identical verified inputs reuse the stored plan, and changed inputs produce the next version. BrandKit editing and licensed-asset management will be exposed by the later campaign workflow; renderer callers already supply resolved, authorized asset files by logical ID. Automated tests replace the director with a fake and never call paid services.

Narration generation defaults to OpenAI text to speech and uses the existing `OpenAI__ApiKey`; no separate voice-provider credential is needed. The defaults are `gpt-4o-mini-tts`, the `marin` voice, and a warm real-estate narration instruction. The provider requests documented 24 kHz, 16-bit little-endian PCM and wraps it in a standards-compliant WAV container locally. Set `Voice__Provider=OpenAI` explicitly when overriding configuration. OpenAI narration is treated as one continuous audio track across the planned narration window because the speech endpoint does not return segment timing metadata.

An owner can optionally upload one private TXT, Markdown, or DOCX narration script from the property page. Uploading stores and previews the extracted text but does not use it in a video. To authorize its use, check **I confirm this script is accurate and approved for real-estate marketing**. Accepted scripts may contain 5–2,000 words. Scripts up to 145 words replace the short campaign narration; a longer accepted script enables the optional **Full narrated tour** deliverable, whose estimated duration preserves the complete script at roughly 145 spoken words per minute with opening and closing room. Replacing a document clears acceptance and requires a fresh review. Generate or refresh the campaign after accepting or revoking: existing videos never change in place.

ElevenLabs remains available as an alternative by setting `Voice__Provider=ElevenLabs`, `Voice__ApiKey`, and `Voice__VoiceId`; its default model is `eleven_multilingual_v2` and default format is `mp3_44100_128`. Local development stores ignored assets beneath the shared repository-level `App_Data/campaign-assets` folder; the Azure provider uses the configured private Blob container. Identical narration input reuses the existing asset, while a changed provider, voice, model, output, or instruction configuration creates a new immutable version. The campaign UI discloses that narration uses an AI voice. Automated tests always use a fake provider.

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

On a property's Overview tab, **External listing research** accepts a pasted or dragged canonical Zillow home-details
link. The agent must confirm that the link is for the displayed property address. Listing Studio stores only the
normalized URL, Zillow property ID, confirmation audit fields, and tenant ownership; tracking query values are removed
and Zillow page content is not scraped. The saved link opens Zillow for market research. Automated facts, Zestimate,
photo, or video imports remain off until an approved Zillow/Bridge or MLS agreement is configured. Continue uploading
original photos and videos that the agent owns or is licensed to use; a public listing page does not by itself grant
reuse rights for downloadable marketing videos.

Neighborhood Insights is optional and disabled by default to prevent accidental Google Places charges. Enable Places API (New) in a billing-enabled Google Cloud project, restrict the key to that API and the Web host's server addresses, then store it outside source control with `NeighborhoodInsights__ApiKey` and set `NeighborhoodInsights__Enabled=true`. The property page can then retrieve nearby schools, parks, libraries, groceries, healthcare, dining, shopping and transit. Results show straight-line distance, source links, current provider photos and attribution. A signed-in owner must approve each fact before it enters the next generated story or campaign. School proximity never asserts attendance assignment or quality, and generated copy rejects family-suitability, safety, demographic, rating and school-boundary claims. Stored place facts are tenant-scoped; Google photo references are fetched on demand for in-app review and are never cached or exported.

To include neighborhood imagery in downloadable campaign videos, approve the place and upload a separate JPG, PNG, or WEBP image that you own or are licensed to use. Enter the required photo credit before upload. The private, tenant-scoped asset is timed to the matching grounded neighborhood fact and its credit is burned into every generated MP4 that uses it. Refreshing Google place results preserves the uploaded asset when the provider place ID remains present. If an approved place has no licensed video photo, the campaign still uses its factual words but keeps the property photo as the visual background.

A property's details page accepts up to 50 JPG, JPEG, PNG, or WEBP originals of at most 20 MB each. It displays thumbnails and metadata, reports upload progress, retries failed selections, and supports drag-and-drop or accessible arrow-button reordering. Archived properties and their photos are read-only and hidden from the default dashboard; select **Show archived** and refresh to include them. Property and media queries derive organization scope from the authenticated user's membership, and never accept an organization identifier from the browser.

When media analysis is enabled, the Worker claims pending photos with a PostgreSQL work lease, stores structured category, room type, quality, hero suitability, visibility flags, potential problems, factual description, and suggested ordering, and schedules bounded retries. Completed results and failed-attempt state appear on the property details page; an owner can explicitly requeue a failed analysis.

After all property photos show completed analysis, use **Generate story** on the property details page to create campaign title, hook, narrative, highlights, voiceover, closing call to action, and long and short social captions. Generated output is immutable and versioned. Changing verified inputs produces a new version; unchanged inputs return the stored story without a new AI request. A configured provider credential is required for this manual workflow.

Use the Campaign tab's **Choose your deliverables** section before generating. Agents can select TikTok, Instagram Reel, YouTube Short, a horizontal YouTube property tour, and—when an accepted script exceeds 145 words—a full narrated tour. At least one selection is required. The three short-social destinations use a shared 30-second 1080x1920 vertical edit, while the YouTube property tour uses a 60-second 1920x1080 horizontal edit. Only selected deliverables are exposed, and all standard formats generated together count as one successful property campaign rather than one campaign per format. Existing legacy campaign records remain readable.

Use **Generate campaign** to enqueue the complete background workflow. The Web request returns immediately; the Worker validates the listing, analyzes pending media, generates or reuses the grounded story, master plan, narration and selected derivatives, stores social copy, and finalizes the campaign. In the Media tab, agents can label uploaded walkthrough clips by verified tour stop and move them into a logical sequence such as front exterior, entry, living room, kitchen, bedrooms, bathrooms, and outdoor space. Direction aligns narration passages with those verified labels, follows the agent's order, and fills unavailable sections with relevant analyzed photos. OpenAI speech input is chunked safely for long scripts and combined into one narration asset. Walkthrough source audio is not mixed into the deliverable. The page polls durable status every two seconds and shows every checkpoint plus retry, cancellation, organization-scoped inline previews, and explicit downloads.

Campaign generation is enabled by default. `CampaignGeneration:PollIntervalSeconds` controls Worker polling, `StageTimeoutSeconds` bounds one checkpoint, and `LeaseSeconds` controls interrupted-work recovery. Start both Web and Worker for this workflow. A checkpoint is committed before the next starts, automatic failures receive at most three attempts, and an explicit retry resumes at the failed checkpoint without repeating completed expensive provider calls. Unexpected polling/database failures are logged and retried rather than terminating the Worker. An unchanged property/media input reuses its completed campaign. Live OpenAI checkpoints require `OpenAI__ApiKey`; ElevenLabs additionally requires its runtime credentials only when selected. Automated integration tests replace every paid provider and the renderer with deterministic fakes.

Uploads are capped at 20 MiB, 15,000 pixels on either side, and 50 million pixels total. The server reads no more than one byte beyond the client-declared length when detecting a size mismatch.

Selective image-to-video generation is disabled by default. When `AIVideo:Enabled=true`, configure `Provider`, `ApiKey`, `Endpoint`, `Model`, and `GenerationVersion` at runtime. The endpoint must be an HTTPS synchronous multipart gateway accepting `image`, `prompt`, `duration_seconds`, `aspect_ratio`, and `model`, and honoring the supplied `Idempotency-Key` header. It returns an MP4 body plus `X-Video-Width`, `X-Video-Height`, and `X-Video-Duration-Ms` headers, and may also return `X-Provider-Request-Id`, `X-Provider-Model`, and `X-Estimated-Cost-Usd`. Set `RequestTimeoutSeconds` and `MaxOutputMegabytes` to bound the call. Only production-plan scenes explicitly marked for generative motion invoke the gateway. Identical media/instruction/duration/aspect/version input reuses the private cached clip and its recorded provider, model, request, metadata, and estimated cost. If the feature is disabled, marked scenes render from their original property-photo fallback with no provider call.

Storyboard walk-through clips are disabled by default. Set `Higgsfield:Enabled=true` with `Higgsfield:ApiKey` (the key ID) and `Higgsfield:ApiSecret` from the Higgsfield console on both the Web and Worker hosts, for example as `Higgsfield__ApiKey` and `Higgsfield__ApiSecret` secrets. The Media tab then offers to create a clip for each pair of consecutive shots: the Worker uploads both photos to Higgsfield, requests `Model` (default `kling-video/v3.0/std/image-to-video`) with the first photo as `image_url` and the next as `last_image_url`, polls until the clip is ready, and stores it with the campaign assets. Each source shot gets a play button for its clip, and campaign renders play a finished clip at the end of the first photo's scene, keeping scene timing, whenever two consecutive scenes show exactly that pair. Without a clip, or when a scene is too short to fit one at up to double speed, the render keeps its planned still transition. Changing `Model`, `ClipDurationSeconds`, or `GenerationVersion` creates new clips instead of reusing old ones.

Each photo on the Media tab has a Fix button. Fix brightness and color runs in the Worker with FFmpeg and no paid service: it applies one gentle white-balance, levels and exposure curve to the whole photo, so nothing in it is added or moved. Remove clutter is an OpenAI image edit (`PhotoFixes:ImageModel`, default `gpt-image-1`) and is off by default; set `PhotoFixes:RemoveClutterEnabled=true` with `OpenAI:ApiKey` on both hosts (in Azure, deploy with `photoClutterRemovalEnabled=true`). The Worker pads the photo onto the editor's 1536x1024, 1024x1536 or 1024x1024 canvas, asks for movable clutter only to be removed and every permanent feature of the home to stay unchanged, and cuts the result back to the photo's own proportions. An image model redraws the whole frame, so its output is never used as the photo: the Worker compares it with the original and pastes only the small areas it tidied into the full-resolution original. If the edit changed more than a fifth of the frame or moved the room's edges, it is rejected and the photo is left as it was. A fix replaces the photo everywhere it is used (storyboard, map, walk-through clips, story and campaign) and keeps the upload, so the agent can compare the two and revert. The photo is analyzed again after a fix or revert, and only the walk-through clips made from it are regenerated.

### Stripe test-mode billing

Stripe billing is disabled by default and no automated test contacts Stripe. The launch configuration presents a one-campaign, 30-day free trial, Starter at $49/month with four campaigns, and Professional at $99/month with twelve campaigns. These display prices and allowances are runtime configuration; they do not activate checkout or create Stripe products. To exercise billing manually, create two recurring **test-mode** prices in Stripe, then provide `Stripe__SecretKey`, `Stripe__StarterPriceId`, `Stripe__ProfessionalPriceId`, and the corresponding monthly campaign allowances through `.env` or user secrets. Set `Stripe__PublicBaseUrl` to the externally reachable Web origin used for Stripe return URLs, keep `Stripe__ApiBaseUrl=https://api.stripe.com`, set `Stripe__Enabled=true`, and never place live or test credentials in tracked files.

Forward Stripe test events to the exact webhook endpoint and copy the CLI-provided signing secret into runtime configuration:

```bash
stripe login
stripe listen --forward-to https://localhost:5001/billing/stripe-webhook
dotnet user-secrets set --project src/ListingStudio.Web "Stripe:WebhookSecret" "your-cli-signing-secret"
```

Start the Web host, register and sign in, then open `/billing`. Choosing a plan creates a server-side Stripe Customer and Checkout Session; an existing customer can open Stripe's Customer Portal. Use Stripe test card `4242 4242 4242 4242`, any future expiry and any CVC only on Stripe-hosted test checkout. Listing Studio never receives or stores card data. The checkout return page is informational: subscription plan, status and period change only after a valid signed `checkout.session.completed` or `customer.subscription.*` webhook. Webhook event IDs are stored for replay protection and older events cannot replace newer subscription state. A queued or running campaign reserves one available campaign slot, but usage is recorded only when the campaign completes successfully. Failed campaigns and retries do not consume the allowance, and new work is stopped before an unapproved overage can occur.

For a fully local webhook check after checkout, trigger a test update and inspect `/billing` after Stripe delivers it:

```bash
stripe trigger customer.subscription.updated
```

Stripe CLI fixtures may not reference the customer created by your browser checkout. The authoritative end-to-end check is therefore the Dashboard/CLI event for that test customer: confirm a 2xx delivery to `/billing/stripe-webhook`, then verify the plan, status, current period and usage shown in `/billing`. Do not enable live mode or create production resources as part of this test.

CI installs FFmpeg plus a deterministic font, renders a fully branded sample with narration and music, then generates and renders HERO, FEATURE, and TEASER landscape deliverables plus a vertical teaser from one master. One derivative uses a locally generated motion clip through the same typed input used by cached AI-video assets. Every output is probed for its exact duration, dimensions, and codecs. To opt into those tests locally after installing FFmpeg and ffprobe, set `RUN_FFMPEG_E2E=1` before running the integration test suite.

Integration tests require a running Docker daemon. They start a disposable PostgreSQL 17 container and apply the committed migrations automatically.

Stop PostgreSQL with `docker compose down`; add `--volumes` only when you also intend to delete local database data.

## Design system

The brand palette and typography live in the `MudTheme` in `src/ListingStudio.Web/Components/Layout/MainLayout.razor`. `wwwroot/app.css` reads those colours through MudBlazor's `--mud-palette-*` variables and exposes them as `--ls-*` tokens, so a palette change is made in one place. Inter (body) and Fraunces (headings) are self-hosted from `wwwroot/fonts` under the SIL Open Font License; their license files sit next to the font files. No font or style request leaves the application.

## Production deployment

### Platform administration and operational telemetry

Platform administration uses the dedicated ASP.NET Identity `PlatformAdmin` role. It is intentionally separate from
`OrganizationMemberRole`: owning a customer workspace does not grant platform access, and a platform administrator
does not bypass the existing organization-scoped property and media services. `/admin`, `/admin/audit`, and
`/api/admin/dashboard` all require the `PlatformAdministration` policy.

After applying migrations, grant the first role only to an existing account with the controlled command:

```bash
dotnet run --project src/ListingStudio.Web -- --grant-platform-admin admin@example.com
```

To remove it, use `--revoke-platform-admin admin@example.com`. Run either command in a protected operator session with
normal production database configuration, then remove the email from shell history if your environment records
arguments. The command is idempotent, exits without starting the Web host, and writes an append-only audit event with
a one-way user reference rather than an email address. The affected user must sign out and back in so the new role
claim is issued. Do not seed the role from a committed email list or create a default administrator password.

Because production PostgreSQL is private, execute the same command inside the Web container from an authenticated
Azure operator session rather than opening database access. Use Azure Container Apps' console/exec facility, run
`dotnet ListingStudio.Web.dll --grant-platform-admin <existing-email>` in the application directory, verify the
success message, and close the session. Do not put the administrator email into source control, Bicep, image layers,
or persistent application configuration.

The dashboard reads privacy-minimized database aggregates for background-job outcomes, provider failures, sign-ins,
active users, coarse route-group traffic, campaign outcomes, and AI call counts/cost estimates. Configure the
per-call estimates through the protected GitHub `production` environment variables documented in
[DEPLOYMENT.md](DEPLOYMENT.md#ai-cost-estimates). Bicep applies them consistently to Web and Worker. A zero value means
no estimate has been configured, and changes affect only future calls. Update the estimates when provider pricing or
the typical request shape changes. Existing Application Insights/OpenTelemetry remains the source for correlated
infrastructure diagnostics; the in-product dashboard deliberately does not query or display raw traces.

A platform administrator can use **Reset metrics** on `/admin` after changing cost assumptions or beginning a new
reporting period. Resetting creates a new reporting baseline: dashboard totals include only activity at or after that
time, while operational history, customer data, campaign records, billing data, and the append-only audit log remain
untouched. Each reset is itself recorded with a one-way administrator reference. New traffic and provider calls can
therefore appear immediately after a reset.

Operational records never include credentials, full URLs, query strings, IP addresses, user agents, prompts,
provider payloads, property IDs, organization IDs, emails, or customer media. Active users are derived from the
account's latest successful sign-in timestamp. This operational collection is for security, reliability, and cost
control. Any future optional product analytics (funnels, behavior tracking, replay, advertising attribution, or
cross-session profiling) must be disabled by default until a documented consent, retention, deletion, and regional
privacy review is approved. Access to the admin dashboard does not waive those requirements.

The repository contains separate production containers for Web, Worker and the EF migration bundle, plus Bicep for Azure Container Apps, PostgreSQL Flexible Server, Blob Storage, ACR, Application Insights, Log Analytics, Key Vault, managed identity and private database networking. Production hosts expose `/health/live` and `/health/ready`; readiness checks PostgreSQL and the container-installed FFmpeg binary. Production logging is structured JSON and is exported through Azure Monitor OpenTelemetry when `APPLICATIONINSIGHTS_CONNECTION_STRING` is configured.

Deployment is intentionally manual and approval-gated through the GitHub `production` environment. It uses OIDC rather than a stored Azure client secret, pushes only immutable commit-SHA images, runs migrations as a one-off Container Apps Job, and enables the Worker only after migration succeeds. No Azure resources are created by normal CI or by merging a pull request. See [DEPLOYMENT.md](DEPLOYMENT.md) for setup, deployment, secret configuration, verification and rollback.
