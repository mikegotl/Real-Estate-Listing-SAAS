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

## Property marketing story slice

The Domain owns the versioned, tenant-scoped `PropertyStory` and its structured campaign copy. Application defines the provider-neutral generator contract, source records, and a deterministic grounding validator. Infrastructure assembles verified property data, completed media observations, and organization branding; it never treats media observations as authoritative property facts. Stories are stored only after required-field, length, unsupported-number, and protected-claim checks pass.

The source fingerprint includes the verified inputs and generator version. Repeating a request with identical inputs returns the stored result without another provider call; changed property data, media analysis, branding, or prompt version creates the next immutable story version. Composite tenant foreign keys and organization-scoped queries prevent cross-organization reads or generation.

`ListingStudio.AI` implements story generation through the OpenAI Responses API with strict JSON-schema output and response storage disabled. The adapter sends organization branding but does not substitute an account email for an agent display name. A later BrandKit milestone can populate explicit agent branding. Story generation is user-initiated in this slice; the durable campaign workflow planned for Week 12 will orchestrate it asynchronously.

## Video production specification slice

The Domain owns the provider-neutral `VideoProductionSpecification` contract and immutable, tenant-scoped `VideoProductionPlan`. Application defines the editorial-director port and performs authoritative validation of identities, fixed output profiles, exact duration, media references, transitions, crop bounds, safe zones, narration overlap, approved assets, CTA placement, and grounded text. Infrastructure assembles verified property data, the current story, completed media analysis, and organization branding, then persists validated specifications as PostgreSQL `jsonb`.

`ListingStudio.AI` implements an OpenAI editorial director through strict Responses API structured output. The provider chooses only the audio and scene plan from supplied IDs and fact bindings; Application code supplies identity, tenancy, facts, brand, safe zone, CTA, and encoding settings around that projection. The source fingerprint includes all verified inputs plus schema and director versions. Identical inputs reuse the stored plan, while changed inputs create the next immutable version for the requested duration and aspect ratio. The future renderer consumes only a validated specification and never calls AI.

The canonical JSON schema is embedded into Application and is used both for complete-plan validation and the provider's editorial projection, keeping enum vocabularies and numeric bounds aligned. Missing/null/unknown/duplicate fields and incomplete provider results are rejected before persistence. Stored story claims are rechecked against the current verified property data before the director is called. Generation locks the tenant-scoped property and media rows across one bounded provider call and persistence, so concurrent identical requests reuse one plan and edits cannot race the source snapshot. Cancellation/failure releases the transaction. This synchronous flow will move under the Week 12 campaign orchestrator; it intentionally holds database locks while direction is in progress.

## Narration and campaign audio slice

Application defines the provider-neutral `IVoiceProvider`, voice request/result records, character and segment timing metadata, campaign-asset storage port, and organization-scoped narration service. `ListingStudio.AI` implements ElevenLabs through the timestamped text-to-speech endpoint, maps provider JSON into those records, and retries only bounded transient HTTP failures. API keys, provider DTOs, voice identifiers, and HTTP details never enter Domain or persisted timing metadata.

Infrastructure stores immutable `VideoNarration` metadata in PostgreSQL and generated audio through a campaign-asset adapter backed by ignored local files or private Azure Blob Storage. Narration source fingerprints include the production plan, exact narration segments, and voice generation version; identical input reuses the stored asset while a voice/model/output change creates the next version. Measured segment timing must fit the planned interval within a small tolerance. Every metadata query and audio read derives organization scope from authenticated membership, with composite database foreign keys preventing cross-tenant plan associations.

## Deterministic video rendering slice

Application defines the provider-neutral `IVideoRenderer` contract and typed media, narration, request, result, and failure records. `ListingStudio.Video` translates a validated production specification into an FFmpeg argument list without invoking a shell. File paths remain individual process arguments; filter expressions are generated only from finite enums and validated numeric values. The first renderer accepts the fixed 1920x1080, 30fps, H.264/AAC profile and still-image sources, applying exact viewport crops, Ken Burns motion, cuts, crossfades or dip-to-black transitions, and narration placement from measured segment timing.

Incoming transitions consume the first part of the destination scene without shortening the program. The command builder therefore extends the previous still through that overlap and trims the final timeline to the authoritative requested duration. FFmpeg stdout, stderr, exit code, and wall-clock render duration are captured; cancellation and bounded timeouts terminate the complete process tree and partial outputs are removed. Text/logo overlays, music mixing, persisted render jobs, and orchestration remain later milestones.

## Tests

Unit tests target inward layers, grounding rules, specification validation, AI/voice adapters through in-memory HTTP handlers, and deterministic FFmpeg command generation. Integration tests use disposable PostgreSQL 17 containers, apply real EF Core migrations, and exercise the ASP.NET Core composition root, Identity, organization persistence, property lifecycle, property-media storage and ordering, upload limits, durable fake-backed analysis, story, video-plan, and narration persistence and versioning, provider-result rejection, caching, retry, asset reads, and tenant isolation. CI also renders generated sample image/audio media through real FFmpeg and verifies the resulting MP4 streams with ffprobe. Automated tests never call paid external services. Additional module-specific test projects can be introduced beside these as features are added.
