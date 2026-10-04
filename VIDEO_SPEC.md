# Video Production Specification 1.0

**Status:** Proposed for owner approval. This document is a design contract, not an implemented feature.

This specification defines the versioned editorial plan that connects Listing Studio's verified property data, grounded property story, analyzed media, brand assets, narration, and deterministic FFmpeg renderer. The renderer must be able to execute an accepted specification without calling AI or making editorial decisions.

The canonical machine-readable schema is [docs/video-production-spec.schema.json](docs/video-production-spec.schema.json). The synthetic 60-second example is [docs/samples/video-production-spec-60s.json](docs/samples/video-production-spec-60s.json).

## Goals

- Represent complete 60, 30, and 15 second editorial plans.
- Support landscape `16:9` and vertical `9:16` output.
- Reference listing media by `PropertyMedia.Id`, never filenames, blob paths, or public URLs.
- Describe deterministic timing, transitions, Ken Burns motion, overlays, logos, narration, music, CTA, output encoding, and safe zones.
- Support optional generated video without making it a dependency of the normal photo pipeline.
- Keep every displayed or spoken property claim grounded in verified property data, an accepted `PropertyStory`, or an explicit `BrandKit` value.
- Preserve immutable schema, content, source, and director versions for reproducibility.

## Non-goals

- This version does not define FFmpeg command construction. Week 9 maps accepted values to commands.
- This version does not generate narration audio. Week 8 turns narration segments into audio and timing metadata.
- This version does not define final brand typography or audio mastering. Week 10 supplies templates and richer brand assets.
- This version does not require generative video. Week 13 may resolve explicitly marked requests; every such request has a normal property-photo fallback.
- This version does not allow arbitrary renderer filters, shell fragments, font paths, media paths, or provider payloads.

## Document shape

The root `VideoProductionSpecification` contains:

| Field | Purpose |
| --- | --- |
| `schemaVersion` | Contract version. Version 1 uses `1.0`. |
| `propertyId` | Server-owned property identity. |
| `propertyStoryId` / `propertyStoryVersion` | Exact grounded story used by the plan. |
| `requestedDurationSeconds` | One of `15`, `30`, or `60`. |
| `aspectRatio` | `16:9` or `9:16`. |
| `output` | Resolved deterministic output/codec profile. |
| `safeZone` | Normalized rectangle in which overlays must remain. |
| `factBindings` | Server-authoritative allowed text values and their sources. |
| `brand` | Snapshot of the supplied brand values and logical asset IDs. |
| `callToAction` | Exact grounded closing CTA. |
| `audio` | Planned narration and music timeline. |
| `scenes` | Ordered visual timeline and all scene-level decisions. |

Coordinates are normalized decimals from `0` through `1`. Time is always an integer number of milliseconds. IDs are logical database or asset identifiers, never filesystem locations.

## Output profiles

Version 1 supports exactly these deterministic profiles:

| Aspect | Width | Height | FPS | Video | Audio | Pixel format |
| --- | ---: | ---: | ---: | --- | --- | --- |
| `16:9` | 1920 | 1080 | 30 | H.264 | AAC, 48 kHz stereo | `yuv420p` |
| `9:16` | 1080 | 1920 | 30 | H.264 | AAC, 48 kHz stereo | `yuv420p` |

The Application validator resolves and verifies the profile. The AI does not select codecs, dimensions, frame rate, sample rate, channels, or pixel format.

Recommended safe zones are `(0.05, 0.05, 0.90, 0.90)` for `16:9` and `(0.075, 0.05, 0.85, 0.90)` for `9:16`. Templates may become stricter, but never looser than the platform delivery requirement.

## Timeline semantics

- Scenes are numbered consecutively starting at 1.
- The first scene starts at `0`.
- Every later scene starts at the previous scene's `startMs + durationMs`; gaps and negative overlap are invalid.
- The last scene must end at `requestedDurationSeconds * 1000` exactly.
- `durationMs` includes the scene's incoming transition allocation.
- `transitionIn` blends the previous visual into the destination scene during the first `durationMs` of the destination scene. It does not change the program length.
- The first scene uses `cut` with a duration of `0`.
- A `cut` always has duration `0`. `crossfade` and `dipToBlack` use 1-1500 ms and may not exceed one third of either adjacent scene.
- Text/logo overlay offsets are relative to their containing scene. Narration and music times are absolute program times.

These rules avoid ambiguous FFmpeg duration arithmetic. The renderer receives absolute scene starts and never infers editorial timing.

## Visual sources and fallback

`visualSource.kind` determines how the visual is resolved:

| Kind | Required fields | Meaning |
| --- | --- | --- |
| `propertyMedia` | `propertyMediaId` | Use the authenticated original listing image. Other source fields are null. |
| `generatedClip` | `generatedClipId`, `fallbackPropertyMediaId` | Use an already persisted generated clip; fall back to the supplied listing image if the clip is unavailable or invalid. |
| `generativeMotionRequest` | `propertyMediaId`, `fallbackPropertyMediaId`, `generationInstruction` | Marks a future Week 13 request. Until a clip is resolved, the deterministic renderer uses the fallback image. |

Every property-media reference must be among the organization-scoped, completed-analysis inputs supplied to the director. Generated clips must belong to the same organization and property. A generative instruction may describe camera motion only; it may not request architectural, material, furniture, landscaping, person, text, or property-feature changes.

The base photo-animation pipeline therefore remains executable even when AI video is unavailable, rejected, timed out, or not approved for spend.

## Motion

Version 1 permits `none` and `kenBurns` only.

- A viewport is a normalized source-image crop rectangle.
- Both endpoints must remain fully inside the source image.
- For `none`, start and end viewports must be identical.
- For `kenBurns`, the renderer linearly or ease-in-out interpolates the crop between the two endpoints for exactly the scene duration.
- The validator uses the stored source width/height and output aspect ratio to ensure each crop can fill the output without stretching or exposing pixels outside the image.
- Motion must remain subtle: an endpoint may not zoom more than 20% relative to the other endpoint in version 1.

The director chooses allowed viewport endpoints and easing. The renderer does not reframe subjects or ask AI where to crop.

## Grounded text

`factBindings` is a server-authoritative allow-list assembled before the provider call. Each entry has a stable key, exact display value, source type, and source field reference.

Allowed sources are:

- `verifiedProperty`: exact property fields or deterministic formatting of those fields.
- `propertyStory`: exact accepted story fields or exact excerpts of the story voiceover.
- `brandKit`: explicit organization/agent brand values.

Every text overlay, narration segment, and CTA must provide a `groundingKey`. The following rules apply:

1. The binding set is composed by Application code, not invented by the model.
2. Provider output may select a supplied binding but may not add, alter, or omit the authoritative binding definition.
3. Overlay and CTA text must exactly equal the referenced binding value.
4. Narration text must exactly equal a supplied narration binding or a contiguous excerpt explicitly supplied as a binding.
5. Property media descriptions may guide visual order, but they never authorize a property claim.
6. Unsupported numbers, property features, location claims, financial claims, or brand details invalidate the complete plan before persistence.

This deliberately trades some copy flexibility for auditability and prevents the renderer from displaying or speaking unverified facts.

## Overlays, logos, and CTA

- Text overlays use a finite `styleToken`: `openingTitle`, `propertyFact`, `lowerThird`, or `closingCta`.
- Style tokens refer to renderer-owned templates; AI cannot supply fonts, filter expressions, colors, paths, or shell fragments.
- Overlay rectangles must be contained by the root safe zone and remain within their scene duration.
- Logo overlays reference only the primary or secondary logical asset ID present in the brand snapshot.
- Logo boxes must be inside the safe zone, preserve source aspect ratio, and have opacity from 0 through 1.
- The last scene must contain a `closingCta` overlay whose text and grounding key match the root CTA.

## Audio plan

Narration segments define planned timing before TTS is generated. Each segment has a unique ID, absolute start, planned duration, exact grounded text, and grounding key. Scenes list the narration IDs that overlap their visual interval.

Week 8 may attach generated audio and measured timing to these IDs. If measured narration cannot fit its planned interval within configured tolerance, the workflow must fail validation or create a new specification version; the renderer must not silently speed up, truncate, or rewrite narration.

Music uses a logical, pre-approved asset ID. `null` means no music and requires mood `none` with zero duration. A non-null asset must be available to the organization and licensed for the campaign. Gain, fade, and ducking values are explicit and become deterministic renderer inputs.

## AI, Application, and renderer boundary

| Concern | AI video director | Application/Domain | Renderer |
| --- | --- | --- | --- |
| Select/order supplied media | Decides | Validates IDs and tenant | Executes |
| Scene timing | Proposes | Validates exact timeline | Executes exact milliseconds |
| Transition/motion | Selects finite values | Validates supported values/bounds | Maps to known filters |
| Marketing text | Selects supplied bindings | Owns bindings and grounding checks | Renders exact text |
| Narration | Aligns supplied excerpts | Validates text/timing references | Mixes supplied audio later |
| Output/codec profile | No decision | Resolves fixed profile | Executes fixed profile |
| Storage/paths | No access | Resolves authenticated assets | Receives safe local inputs |
| AI/video provider calls | OpenAI adapter only | Coordinates ports | Prohibited |
| FFmpeg commands | Prohibited | Strongly typed contract | Builds from allow-listed values |

The OpenAI adapter should request a strict `DirectedEditorialPlan` projection containing editorial choices only. Application code composes that response with server-owned IDs, output profile, safe zone, fact bindings, and brand snapshot to produce the full specification. This prevents the model from being the authority for identity, tenancy, facts, or encoding settings.

The provider schema must use the Responses API structured `text.format`, require every field, represent optional values with `null`, and set `additionalProperties: false` on every object. These constraints match the official [OpenAI Structured Outputs guidance](https://developers.openai.com/api/docs/guides/structured-outputs).

## Validation rules

Validation occurs after provider deserialization and again before persistence/rendering.

### Identity and tenancy

1. Property, story, media, generated clips, brand assets, and music assets belong to the authenticated organization.
2. `propertyStoryId` belongs to `propertyId`, and its version matches.
3. Every property-media ID was supplied to the director and has completed media analysis.
4. No filenames, blob paths, URLs, provider IDs, or arbitrary local paths appear in the specification.

### Schema and timeline

5. Schema version is supported and all object/enum values are known.
6. Requested duration is exactly 15, 30, or 60 seconds.
7. Aspect ratio and fixed output profile agree.
8. Scene numbers are unique and consecutive; starts are contiguous; durations are positive.
9. The last scene ends at the requested duration exactly.
10. Transitions obey the timeline semantics and duration bounds above.
11. Overlay and narration IDs are unique. References resolve exactly once.
12. Narration segments are inside the program, do not overlap, and are associated with every scene they overlap.
13. Music starts at or after zero and ends no later than the program end. Fade durations fit the music interval.

### Visuals and layout

14. Visual-source field combinations match their `kind` exactly.
15. Generated or requested motion always has a valid property-media fallback.
16. Motion viewports are bounded, aspect-fill-capable, and within the version 1 zoom limit.
17. Text and logo overlays fit their scene and the safe zone.
18. Logo and music asset IDs occur in the supplied approved asset set.

### Grounding

19. Fact bindings exactly equal the server-composed allow-list; keys are unique.
20. Every human-readable overlay, narration segment, and CTA resolves to its stated binding.
21. The closing scene contains the exact root CTA.
22. The existing property-story grounding guard rejects unsupported numeric and protected claims as defense in depth.

Any failure rejects the whole specification. Validation errors must identify the path and rule without logging customer media, provider credentials, or unnecessary personal data.

## Versioning and persistence

Persist an immutable `VideoProductionPlan` aggregate with:

- `Id`
- `OrganizationId`
- `PropertyId`
- `PropertyStoryId`
- `Version` (sequential per property/duration/aspect)
- `SchemaVersion` (`1.0`)
- `DirectorVersion` (for example `openai-video-director-v1`)
- `SourceFingerprint`
- strongly typed specification serialized as PostgreSQL `jsonb`
- `CreatedAtUtc`

`SourceFingerprint` hashes the verified property snapshot, story ID/version, completed media analyses, brand snapshot, approved asset IDs, requested duration/aspect ratio, schema version, and director version. An identical request returns the persisted plan without another paid provider call. Any source, schema, or prompt/director change creates a new immutable version.

Schema versions use semantic versioning:

- Patch changes clarify documentation or validation without changing serialized shape.
- Minor changes add backward-compatible enum values or nullable fields and require renderer capability checks.
- Major changes alter required fields or timing semantics and require a new parser/renderer path.

Never mutate a stored specification to match a newer schema. Migrate by generating a new version.

## C# model recommendations

Keep these provider-neutral types in Domain/Application. Provider request/response DTOs remain in `ListingStudio.AI`.

```csharp
public sealed record VideoProductionSpecification(
    string SchemaVersion,
    Guid PropertyId,
    Guid PropertyStoryId,
    int PropertyStoryVersion,
    RequestedDuration RequestedDuration,
    AspectRatio AspectRatio,
    VideoOutputProfile Output,
    NormalizedRect SafeZone,
    IReadOnlyList<FactBinding> FactBindings,
    VideoBrandPlan Brand,
    GroundedText CallToAction,
    AudioPlan Audio,
    IReadOnlyList<VideoScene> Scenes);

public sealed record VideoScene(
    int SceneNumber,
    int StartMs,
    int DurationMs,
    VisualSource VisualSource,
    TransitionPlan TransitionIn,
    MotionPlan Motion,
    IReadOnlyList<TextOverlay> TextOverlays,
    IReadOnlyList<LogoOverlay> LogoOverlays,
    IReadOnlyList<string> NarrationSegmentIds);

public sealed record VisualSource(
    VisualSourceKind Kind,
    Guid? PropertyMediaId,
    Guid? GeneratedClipId,
    Guid? FallbackPropertyMediaId,
    string? GenerationInstruction);

public enum RequestedDuration { Teaser15 = 15, Feature30 = 30, Hero60 = 60 }
public enum AspectRatio { Landscape16By9, Vertical9By16 }
public enum VisualSourceKind { PropertyMedia, GeneratedClip, GenerativeMotionRequest }
public enum TransitionKind { Cut, Crossfade, DipToBlack }
public enum MotionKind { None, KenBurns }
```

Use integer milliseconds instead of `TimeSpan` in the serialized contract, `decimal` for normalized coordinates/gain values, and enums with explicit JSON string converters. The full model should mirror the canonical schema one-to-one.

Recommended ports:

```csharp
public interface IVideoDirector
{
    string DirectorVersion { get; }

    Task<VideoProductionSpecification> DirectAsync(
        VideoDirectionRequest request,
        CancellationToken cancellationToken = default);
}

public interface IVideoProductionSpecificationValidator
{
    ValidationResult Validate(
        VideoDirectionRequest authoritativeInput,
        VideoProductionSpecification specification);
}

public interface IVideoProductionPlanService
{
    Task<VideoProductionPlanResult?> GenerateAsync(
        string userId,
        Guid propertyId,
        RequestedDuration duration,
        AspectRatio aspectRatio,
        CancellationToken cancellationToken = default);
}
```

`ListingStudio.AI` implements the OpenAI director. Infrastructure implements organization-scoped persistence and source assembly. A future renderer consumes only a validated Domain/Application specification and has no reference to `ListingStudio.AI`.

## Required tests for implementation

- Strict provider request and structured response mapping through an in-memory HTTP handler.
- Valid 60, 30, and 15 second plans in both aspect ratios.
- Exact duration, contiguous timeline, transition, overlay, narration, safe-zone, viewport, and output-profile validation.
- Rejection of unknown, foreign-tenant, pending-analysis, and duplicate media IDs.
- Rejection of unsupported property claims, altered fact bindings, arbitrary overlay/narration text, or unknown brand assets.
- Generated-clip and generative-request fallback validation.
- Persistence, immutable version increments, fingerprint reuse, and cross-organization access.
- A fake-director PostgreSQL integration test proving a validated 60-second plan is stored without renderer-side AI.
- Automated tests never call OpenAI, AI video, TTS, or other paid services.

## Owner approval decisions

Merging the design PR approves these version 1 decisions:

1. Fixed 1080p/30fps H.264/AAC output profiles for `16:9` and `9:16`.
2. Contiguous absolute scene timing with incoming transitions included in destination-scene duration.
3. Finite transition, motion, overlay-style, music-mood, and anchor vocabularies.
4. Exact fact-binding text rather than arbitrary AI-authored overlay/narration claims.
5. Optional generated-motion requests with a mandatory property-photo fallback.
6. Server composition of identity, tenancy, facts, brand, safe-zone, and encoding data around AI editorial choices.
7. Immutable `jsonb` persistence with schema, director, sequential content, and source-fingerprint versioning.

Implementation must not begin until these decisions are approved or amended by the owner.
