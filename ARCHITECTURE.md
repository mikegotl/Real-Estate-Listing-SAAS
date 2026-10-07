# Listing Studio architecture

Listing Studio is a clean modular monolith: one deployable web application and one background worker, with capability adapters kept in independently testable projects.

## Dependency rule

Dependencies point inward:

```text
Domain <- Application <- Infrastructure
                    ^-- AI
                    ^-- Video

Web and Worker are composition roots and may reference Application plus all adapters.
```

- **Domain** contains enterprise rules and has no project dependencies.
- **Application** contains use cases and ports. It references Domain only.
- **Infrastructure** implements persistence, storage, payments, and other technical ports. It references Application and Domain.
- **AI** and **Video** are isolated outbound adapter modules. Each references Application only.
- **Web** hosts Blazor and ASP.NET Core. It composes modules but contains no business rules.
- **Worker** hosts asynchronous/background execution and uses the same module registrations.

Modules expose dependency injection through `AddApplication`, `AddInfrastructure`, `AddAI`, and `AddVideo`. Cross-module implementation references are prohibited; coordination belongs in Application abstractions. PostgreSQL is the transactional store, while external service credentials are supplied at runtime through configuration providers and never committed.

## Identity and tenant data

ASP.NET Core Identity and the EF Core `ApplicationDbContext` are Infrastructure concerns. The framework-independent Domain owns `Organization`, `OrganizationMember`, and membership roles. Application exposes the account-registration use case; Infrastructure implements it with Identity and persists the user, organization, and initial owner membership in one PostgreSQL transaction.

`OrganizationMember` has a unique `(OrganizationId, UserId)` index and foreign keys to both its organization and Identity user. Every future business aggregate must include a required `OrganizationId`, an organization foreign key, and tenant-aware indexes. Application use cases must derive organization scope from the authenticated membership instead of accepting an unrestricted tenant identifier from the client.

Password reset uses Identity data-protection tokens. Development builds keep the generated reset link only in process memory and display it on the confirmation page for local testing; a production email adapter must be configured before production use.

## Property management slice

The Domain owns the `ListingProperty` aggregate, its value-bearing details, property types, listing statuses, validation rules, and archive state. Application exposes provider-neutral property commands and read models through `IPropertyService`. Infrastructure implements that port with EF Core and PostgreSQL; Web renders the authenticated MudBlazor workflows.

Every property has a required `OrganizationId` foreign key and tenant-aware indexes. Property services receive the server-validated Identity user ID, resolve its organization membership, and include that organization in every read and mutation predicate. A property ID is never sufficient authorization. Cross-organization requests therefore return no property and cannot update or archive it. Archiving is a timestamped, read-only state rather than physical deletion, preserving the listing for audit and future workflow history.

## Property media slice

The Domain owns `PropertyMedia` metadata and display-order rules. Application exposes media workflows through `IPropertyMediaService` and the replaceable `IPropertyMediaStorage` port. Infrastructure persists metadata in PostgreSQL and supplies both an Azure Blob Storage adapter and an ignored filesystem adapter for local development. Web streams originals through an authenticated, organization-scoped endpoint instead of exposing provider paths or public containers.

Media rows repeat the required `OrganizationId` and use a composite foreign key to `(PropertyId, OrganizationId)`, so PostgreSQL prevents media from being associated with a property in another tenant. Every list, upload, reorder, delete, content-read, and explicit retry operation also derives the organization from the authenticated membership. Upload validation enforces the supported extension/MIME pairs, a 20 MB per-file limit, image header and dimensions, and a transactional 50-image property limit.

## Property media analysis slice

Application defines the provider-neutral `IPropertyMediaAnalyzer` and `IPropertyMediaAnalysisProcessor` ports. Infrastructure owns durable queue coordination and result persistence. A PostgreSQL `FOR UPDATE SKIP LOCKED` claim changes one eligible row from `Pending` or retryable `Failed` to `Analyzing`, while a ten-minute lease lets another Worker reclaim interrupted work. Each failure records a sanitized error and exponential next-attempt time; automatic processing is capped at three attempts, while an organization-scoped user action can explicitly reset a failed item for another bounded run.

`ListingStudio.AI` implements the analyzer with the OpenAI Responses API. It sends the original image as a data URL, requests strict JSON-schema output, disables response storage, and maps only the allowed structured fields into Domain values. Provider HTTP and JSON types never cross the AI adapter boundary. `ListingStudio.Worker` processes one photo per configured interval and is disabled by default to prevent unapproved API spend. OpenAI credentials and model selection are runtime-only configuration.

## Property video enhancement slice

Property videos use a separate `PropertyVideo` aggregate so image-analysis and still-rendering assumptions remain intact. Originals and enhanced outputs share the private property-media storage port, while metadata and retry state are persisted in tenant-scoped PostgreSQL rows with a composite property/organization foreign key. Application owns the service, transcoder, and queue-processing ports; Infrastructure owns authorization, validated upload orchestration, durable `FOR UPDATE SKIP LOCKED` claims, leases, retries, and persistence; Video owns FFprobe inspection and FFmpeg processing.

Uploads are bounded by type, declared and observed size, dimensions, duration, frame rate, count, and detected container. The original is never modified. Enhancement is a versioned two-pass operation: `vidstabdetect` first records camera motion, then `vidstabtransform` smooths that motion while FFmpeg also applies deflicker, gray-world white balance, temporally smoothed RGB normalization, conservative contrast/saturation correction, constant 30fps cadence, even output dimensions, and H.264/AAC fast-start encoding. Commands use direct argument lists rather than a shell; cancellation and timeouts terminate the process tree, temporary files are removed, sanitized failures are persisted, and partial enhanced objects are deleted before retry.

## Property marketing story slice

The optional neighborhood-insights adapter resolves the verified property address through Google Places, stores a tenant-scoped snapshot of factual nearby-place names, addresses and straight-line distances, and fetches provider photos on demand for attributed in-app review only. Google photo content is not cached or exported. No result enters AI input until an authenticated organization member explicitly approves it. Story prompts and deterministic validation prohibit school-assignment, rating, safety, demographic, family-suitability and other steering claims. Provider credentials remain runtime-only and the integration is disabled by default to prevent unapproved spend.

Approved places can carry a separate tenant-owned neighborhood video photo. The owner must upload an image they own or are licensed to use and provide a required credit. These assets use the private property-media storage adapter, remain organization-scoped, survive place-data refreshes by provider place ID, and participate in campaign fingerprints. The renderer displays each available asset when its exact grounded neighborhood fact is shown or narrated, burns the supplied credit into the frame, and falls back to the property visual when no licensed asset exists.

The Domain owns the versioned, tenant-scoped `PropertyStory` and its structured campaign copy. Application defines the provider-neutral generator contract, source records, and a deterministic grounding validator. Infrastructure assembles verified property data, completed media observations, and organization branding; it never treats media observations as authoritative property facts. Stories are stored only after required-field, length, unsupported-number, and protected-claim checks pass.

The source fingerprint includes the verified inputs and generator version. Repeating a request with identical inputs returns the stored result without another provider call; changed property data, media analysis, branding, or prompt version creates the next immutable story version. Composite tenant foreign keys and organization-scoped queries prevent cross-organization reads or generation.

`ListingStudio.AI` implements story generation through the OpenAI Responses API with strict JSON-schema output and response storage disabled. The adapter sends organization branding but does not substitute an account email for an agent display name. A later BrandKit milestone can populate explicit agent branding. Story generation can be invoked directly or as a checkpoint in the durable campaign workflow.

## Video production specification slice

The Domain owns the provider-neutral `VideoProductionSpecification` contract and immutable, tenant-scoped `VideoProductionPlan`. Application defines the editorial-director port and performs authoritative validation of identities, fixed output profiles, exact duration, media references, transitions, crop bounds, safe zones, narration overlap, approved assets, CTA placement, and grounded text. Infrastructure assembles verified property data, the current story, completed media analysis, and organization branding, then persists validated specifications as PostgreSQL `jsonb`.

`ListingStudio.AI` implements an OpenAI editorial director through strict Responses API structured output. The provider chooses only the audio and scene plan from supplied IDs and fact bindings; Application code supplies identity, tenancy, facts, brand, safe zone, CTA, and encoding settings around that projection. The source fingerprint includes all verified inputs plus schema and director versions. Identical inputs reuse the stored plan, while changed inputs create the next immutable version for the requested duration and aspect ratio. The future renderer consumes only a validated specification and never calls AI.

The canonical JSON schema is embedded into Application and is used both for complete-plan validation and the provider's editorial projection, keeping enum vocabularies and numeric bounds aligned. Missing/null/unknown/duplicate fields and incomplete provider results are rejected before persistence. Stored story claims are rechecked against the current verified property data before the director is called. Generation locks the tenant-scoped property and media rows across one bounded provider call and persistence, so concurrent identical requests reuse one plan and edits cannot race the source snapshot. Cancellation/failure releases the transaction. The campaign orchestrator invokes this bounded operation from its master-plan checkpoint; the service intentionally holds database locks while direction is in progress.

## Narration and campaign audio slice

Application defines the provider-neutral `IVoiceProvider`, voice request/result records, character and segment timing metadata, campaign-asset storage port, and organization-scoped narration service. `ListingStudio.AI` implements ElevenLabs through the timestamped text-to-speech endpoint, maps provider JSON into those records, and retries only bounded transient HTTP failures. API keys, provider DTOs, voice identifiers, and HTTP details never enter Domain or persisted timing metadata.

Infrastructure stores immutable `VideoNarration` metadata in PostgreSQL and generated audio through a campaign-asset adapter backed by ignored local files or private Azure Blob Storage. Narration source fingerprints include the production plan, exact narration segments, and voice generation version; identical input reuses the stored asset while a voice/model/output change creates the next version. Measured segment timing must fit the planned interval within a small tolerance. Every metadata query and audio read derives organization scope from authenticated membership, with composite database foreign keys preventing cross-tenant plan associations.

## Deterministic video rendering slice

Application defines the provider-neutral `IVideoRenderer` contract and typed media, narration, request, result, and failure records. `ListingStudio.Video` translates a validated production specification into an FFmpeg argument list without invoking a shell. File paths remain individual process arguments; filter expressions are generated only from finite enums and validated numeric values. The renderer accepts the fixed 1920x1080 landscape and 1080x1920 vertical, 30fps, H.264/AAC profiles and still-image sources, applying exact viewport crops, Ken Burns motion, cuts, crossfades or dip-to-black transitions, and narration placement from measured segment timing.

Incoming transitions consume the first part of the destination scene without shortening the program. The command builder therefore extends the previous still through that overlap and trims the final timeline to the authoritative requested duration. FFmpeg stdout, stderr, exit code, and wall-clock render duration are captured; cancellation and bounded timeouts terminate the complete process tree and partial outputs are removed.

The Domain `BrandKit` is an immutable production-plan snapshot containing logical logo IDs, agent contact data, and colors; local paths never enter the stored specification. Render requests resolve those IDs to typed logo/music assets after tenant and licensing checks. `VideoBranding` configuration owns the finite renderer templates for opening titles, property facts, lower thirds, closing calls to action, and program fades. The renderer escapes exact grounded text for FFmpeg, positions overlays inside specification-owned boxes, scales logos without changing their aspect ratios, and uses only the BrandKit colors. Music gain, fade-in/out, and narration ducking come from the validated audio plan and are mixed deterministically. Campaign render checkpoints persist the resulting private asset metadata rather than local working paths.

## Campaign derivative slice

Application owns `ICampaignDerivativeGenerator` because derivative planning is deterministic orchestration over the provider-neutral production contract. A validated 60-second master produces separate HERO 60, FEATURE 30, and TEASER 15 specifications for both canonical aspect ratios. Short edits retain the opening and closing scenes, then rank existing generated clips, branding, and longer editorial beats; they do not invoke a director or regenerate paid assets. Every result carries the reused property-media and generated-clip IDs so later orchestration can resolve the same authorized assets.

Scene timings are allocated to the exact requested duration while transitions, narration, music, and overlays are retimed within their scenes. Source viewports are center-cropped inside the master decision for the target aspect ratio, and overlay/logo boxes are proportionally remapped between landscape and vertical safe zones. Generation rejects a non-contiguous master, missing closing CTA, duplicate/unknown media, or invalid source dimensions; generated output is additionally checked for exact timing, crop aspect, CTA preservation, and out-of-zone branding. The campaign workflow persists the selected landscape HERO, FEATURE, and TEASER specifications before rendering.

## Durable campaign generation slice

The Domain owns the tenant-scoped `CampaignGenerationJob`, its twelve ordered checkpoints, lifecycle, bounded-attempt state, cancellation signal, and three persisted deliverables. Application exposes user-facing queue/status/retry/cancel/download operations and a one-checkpoint processor contract. Infrastructure coordinates workers with PostgreSQL row locks and expiring leases. A browser request only creates or reuses a job; the Worker performs provider calls and rendering outside the HTTP lifecycle.

Each successful checkpoint is committed before the next is eligible. Interrupted leases can be reclaimed after a worker restart, provider timeouts schedule a bounded retry, and a terminal failure retains the current checkpoint for explicit resume. The input fingerprint uses stable property and media source identity so an unchanged completed campaign is idempotently reused; mutable analysis progress does not invalidate it. Story, master-plan, and narration services retain their own content fingerprints, preventing a resumed render from repeating completed paid work.

The authenticated Blazor property page polls tenant-scoped status, displays per-stage progress, and exposes cancellation, retry, and private MP4 downloads. Render working files are temporary, while final assets use `ICampaignAssetStorage`. Deterministic photo rendering remains the complete default path.

## Selective generative video slice

Application defines the provider-neutral `IAiVideoProvider` and `IGeneratedVideoClipService` contracts. A generation request contains one authorized property image, one finite camera-motion instruction, duration, and aspect ratio. The Video adapter implements an opt-in synchronous multipart HTTPS gateway contract and owns the fixed real-estate preservation prompt; provider HTTP details do not cross into inward projects. AI video is disabled by default, so no paid request can occur without explicit runtime configuration.

Infrastructure locks the tenant-scoped source-media row, fingerprints the stable media identity plus generation version and exact request, and stores one immutable `GeneratedVideoClip` and private MP4 per unique input. The record includes provider, model, request ID, bounded metadata, and estimated USD cost. Composite organization/property/media foreign keys prevent cross-tenant associations, while the unique source fingerprint prevents duplicate spend under concurrent work.

Only scenes marked `GenerativeMotionRequest` are resolved during the existing campaign checkpoint. Successfully resolved specifications reference the cached clip and retain their property-photo fallback. The renderer consumes typed generated-clip assets directly; an unresolved request or unavailable optional clip continues through the existing still-image animation path. Automated tests use a fake provider and never activate a paid service.

## Subscription billing slice

The Domain owns one `OrganizationBillingAccount` per tenant, subscription plan and status, current billing period, included campaign allowance, total campaign usage, and additional usage. Each newly enqueued campaign also creates an append-only `CampaignUsageRecord` in the same PostgreSQL transaction; idempotent campaign reuse does not consume the allowance twice. Billing accounts and usage records have required organization foreign keys, and all user-facing operations derive their organization from authenticated membership.

Application exposes provider-neutral billing and usage ports. Infrastructure implements Stripe Customer, Checkout Session, Customer Portal and webhook integration over server-to-server HTTPS. Browser redirects never update payment state. Only an HMAC-verified Stripe webhook can apply subscription status, price-to-plan mapping, and current-period dates. Durable `BillingWebhookReceipt` rows make replay idempotent, while the last provider event timestamp prevents an older event from overwriting newer subscription state.

Stripe is disabled by default. Secret keys, webhook signing secrets and test/live price identifiers are runtime configuration only; Listing Studio stores provider identifiers and billing state, never payment-card data. Automated tests use fake checkout/portal URLs and signed test webhook payloads, so they do not create Stripe customers or charges.

## Production deployment slice

Web and Worker publish as separate non-root .NET 10 containers with FFmpeg/ffprobe installed from the runtime distribution. The Worker hosts an internal HTTP health surface alongside its background services. Both hosts expose process liveness and dependency readiness; readiness verifies PostgreSQL connectivity and executes the configured FFmpeg binary with a bounded timeout. Production console logs use structured JSON, while the Azure Monitor OpenTelemetry distro exports correlated telemetry to workspace-backed Application Insights when its runtime connection string is present.

Azure Container Apps runs the public Web revision, private Worker and manual EF migration job. PostgreSQL Flexible Server has no public endpoint and is reachable only through delegated virtual-network subnets and private DNS. Blob assets remain private and use `DefaultAzureCredential` plus one user-assigned runtime identity; storage shared-key access is disabled. The same identity pulls immutable images from ACR and resolves the PostgreSQL password from RBAC-enabled Key Vault. No provider credential is embedded in Bicep, images or source.

The production GitHub workflow is dispatch-only, targets a reviewer-protected environment and uses GitHub OIDC for Azure authentication. It restores, builds, tests, publishes, validates Bicep, pushes three commit-SHA images, deploys revisions, executes the migration job exactly once, enables the Worker and verifies health. Normal pull-request CI performs every local validation and container smoke check but cannot deploy. Web uses multiple revisions for traffic rollback; schema rollback remains an explicit data-risk decision and normally uses a forward fix.

## Tests

Unit tests target inward layers, grounding rules, specification validation, AI/voice/video and Stripe adapters through in-memory HTTP handlers, campaign job transitions, derivative selection/reframing/asset reuse, and deterministic FFmpeg command generation. Integration tests use disposable PostgreSQL 17 containers, apply real EF Core migrations, and exercise the ASP.NET Core composition root, Identity, organization persistence, property lifecycle, property-media storage and ordering, upload limits, durable fake-backed analysis, story, video-plan, narration, selective generated-clip caching, subscription billing, signed idempotent webhooks, usage tracking, and complete campaign orchestration, including checkpoint retry, cancellation, downloads, and tenant isolation. CI also renders generated sample image/audio media plus landscape and vertical derivatives through real FFmpeg and verifies the resulting MP4 streams with ffprobe, including a resolved generated-video scene. Automated tests never call paid external services. Additional module-specific test projects can be introduced beside these as features are added.
