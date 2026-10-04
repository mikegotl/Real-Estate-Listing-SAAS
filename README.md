# Listing Studio

.NET 10 foundation for a real-estate Listing Studio, implemented as a clean modular monolith with a Blazor Web App, an ASP.NET Core background worker, PostgreSQL persistence, organization tenancy, ASP.NET Core Identity, and a MudBlazor property-management workspace.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (the SDK is pinned in `global.json`)
- Docker with Docker Compose v2 (for PostgreSQL)
- FFmpeg and ffprobe 6 or newer (for deterministic video rendering)

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
```

The same runtime configuration sections are available to both hosts: `PostgreSQL`, `AzureBlobStorage`, `OpenAI`, `AIVideo`, `Voice`, `FFmpeg`, `VideoBranding`, and `Stripe`.

Listing photos use the provider selected by `AzureBlobStorage:Provider`. The default `Local` provider writes ignored development files beneath `App_Data/property-media`; set the provider to `Azure` and supply `AzureBlobStorage:ConnectionString` at runtime to use the configured Blob container. Do not put the Azure connection string in tracked settings.

Property-photo analysis runs only in the Worker and is disabled by default so local startup cannot accidentally spend API credits. To enable it, provide `OpenAI__ApiKey` and a vision-capable `OpenAI__Model`, then set `MediaAnalysis__Enabled=true`. The Worker processes one photo at a time, waits the configured polling interval between requests, and retries transient failures up to three times. Automated tests always replace the provider with a fake and never call OpenAI.

Property marketing stories are generated on demand after every uploaded image has completed analysis. Story generation uses the same runtime-only OpenAI key and model, requests strict structured output, and validates the result against verified property facts before saving it. Identical verified inputs reuse the existing version to avoid another provider request. The initial branding input is the organization name; explicit agent profile branding is deferred to BrandKit. Automated tests use a fake story generator and never spend API credits.

Video production plans can be generated after a grounded property story exists. The OpenAI video director returns editorial choices only; server code supplies and validates property/story identity, media IDs, exact fact bindings, BrandKit values, output profile, safe zone, and CTA before storing an immutable specification. Stored story claims must still pass grounding against current verified facts; regenerate the story if edits invalidate its claims. Invalid structured responses, schema drift, and requests to change property features are rejected. Concurrent identical requests reuse a single provider call. Plans support 60, 30, and 15 seconds in landscape or vertical format. Identical verified inputs reuse the stored plan, and changed inputs produce the next version. BrandKit editing and licensed-asset management will be exposed by the later campaign workflow; renderer callers already supply resolved, authorized asset files by logical ID. Automated tests replace the director with a fake and never call paid services.

Narration generation uses the configured ElevenLabs timestamp endpoint and stores MP3 audio plus character/segment timing through the campaign-asset storage adapter. Set `Voice__Provider=ElevenLabs`, `Voice__ApiKey`, and `Voice__VoiceId` at runtime; the default model is `eleven_multilingual_v2` and the default format is `mp3_44100_128`. Local development stores ignored assets beneath `App_Data/campaign-assets`; the Azure provider uses the configured private Blob container. Identical narration input reuses the existing asset, while a changed voice/model/output configuration creates a new immutable version. Automated tests always use a fake provider.

The deterministic renderer is configured through `FFmpeg:ExecutablePath`, `FFmpeg:ProbeExecutablePath`, `FFmpeg:RenderTimeoutSeconds`, and the `VideoBranding` template section. It accepts typed still-image, narration, logo, and licensed-music assets and produces a 1920x1080, 30fps H.264/AAC MP4. It supports exact scale/crop, Ken Burns motion, cuts, crossfades, dip-to-black transitions, grounded opening titles/property facts/lower thirds/closing CTAs, agent and brokerage logos, configurable whole-video fades, measured narration, background-music fades, and planned narration ducking. Brand colors and contact values come from the specification's BrandKit; font sizes, padding, opacity, color roles, and program fades come from configuration rather than hard-coded campaign values. Set `VideoBranding:FontFilePath` to an installed TrueType/OpenType font when the FFmpeg environment cannot resolve the default `Sans` family. Arguments are passed directly to FFmpeg without a shell. Rendering fails rather than overwriting an existing output, captures process diagnostics and duration, honors cancellation, and removes partial output after failure or timeout.

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

After signing in, open `/properties` to create, list, inspect, edit, and archive properties. A property's details page accepts up to 50 JPG, JPEG, PNG, or WEBP originals of at most 20 MB each. It displays thumbnails and metadata, reports upload progress, retries failed selections, and supports drag-and-drop or accessible arrow-button reordering. Archived properties and their photos are read-only and hidden from the default dashboard; select **Show archived** and refresh to include them. Property and media queries derive organization scope from the authenticated user's membership, and never accept an organization identifier from the browser.

When media analysis is enabled, the Worker claims pending photos with a PostgreSQL work lease, stores structured category, room type, quality, hero suitability, visibility flags, potential problems, factual description, and suggested ordering, and schedules bounded retries. Completed results and failed-attempt state appear on the property details page; an owner can explicitly requeue a failed analysis.

After all property photos show completed analysis, use **Generate story** on the property details page to create campaign title, hook, narrative, highlights, voiceover, closing call to action, and long and short social captions. Generated output is immutable and versioned. Changing verified inputs produces a new version; unchanged inputs return the stored story without a new AI request. A configured provider credential is required for this manual workflow.

The Week 7 video-plan service is currently an application API rather than a Blazor workflow. It creates a fully validated, renderer-ready production specification from the latest grounded story and analyzed media. A configured OpenAI credential is required outside automated tests. The future campaign workflow will expose and orchestrate this service.

The Week 8 narration service is also an application API pending the later campaign workflow. It converts a stored plan's grounded narration segments into a tenant-scoped audio asset and measured timing metadata, rejecting generated segments that no longer fit their planned intervals. No live voice request runs unless the service is explicitly invoked with valid runtime credentials.

The Week 10 renderer remains an application API pending background campaign orchestration. Callers must resolve tenant-authorized logo assets and licensed music to local files before invoking it; filesystem paths are never stored in the production specification. Vertical output, persistent render jobs, and delivery remain later milestones. CI installs FFmpeg plus a deterministic font and renders a fully branded sample with narration and music. To opt into that test locally after installing FFmpeg and ffprobe, set `RUN_FFMPEG_E2E=1` before running the integration test suite.

Integration tests require a running Docker daemon. They start a disposable PostgreSQL 17 container and apply the committed migrations automatically.

Stop PostgreSQL with `docker compose down`; add `--volumes` only when you also intend to delete local database data.
