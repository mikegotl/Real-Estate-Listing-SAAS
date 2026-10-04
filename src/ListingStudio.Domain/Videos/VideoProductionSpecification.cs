using System.Text.Json.Serialization;

namespace ListingStudio.Domain.Videos;

public enum RequestedDuration
{
    Teaser15 = 15,
    Feature30 = 30,
    Hero60 = 60,
}

public enum VideoAspectRatio
{
    Landscape16By9,
    Vertical9By16,
}

public enum FactSource
{
    VerifiedProperty,
    PropertyStory,
    BrandKit,
}

public enum VisualSourceKind
{
    PropertyMedia,
    GeneratedClip,
    GenerativeMotionRequest,
}

public enum TransitionKind
{
    Cut,
    Crossfade,
    DipToBlack,
}

public enum MotionKind
{
    None,
    KenBurns,
}

public enum MotionEasing
{
    Linear,
    EaseInOut,
}

public enum OverlayAnchor
{
    TopLeft,
    TopCenter,
    TopRight,
    Center,
    BottomLeft,
    BottomCenter,
    BottomRight,
}

public enum TextOverlayStyle
{
    OpeningTitle,
    PropertyFact,
    LowerThird,
    ClosingCta,
}

public enum MusicMood
{
    None,
    WarmCinematic,
    ModernAmbient,
    UpbeatLifestyle,
}

public sealed record NormalizedRect(decimal X, decimal Y, decimal Width, decimal Height);

public sealed record VideoOutputProfile(
    int Width,
    int Height,
    int FrameRate,
    string VideoCodec,
    string AudioCodec,
    string PixelFormat,
    int SampleRateHz,
    int AudioChannels);

public sealed record FactBinding(string Key, string Value, FactSource Source, string SourceReference);

public sealed record VideoBrandPlan(
    string? PrimaryLogoAssetId,
    string? SecondaryLogoAssetId,
    string? AgentName,
    string? Phone,
    string? Email,
    string? Website,
    string PrimaryColor,
    string SecondaryColor);

public sealed record GroundedText(string Text, string GroundingKey);

public sealed record MusicPlan(
    string? AssetId,
    MusicMood Mood,
    int StartMs,
    int DurationMs,
    decimal GainDb,
    int FadeInMs,
    int FadeOutMs,
    decimal DuckingGainDb);

public sealed record NarrationSegment(
    string Id,
    int StartMs,
    int DurationMs,
    string Text,
    string GroundingKey);

public sealed record AudioPlan(IReadOnlyList<NarrationSegment> NarrationSegments, MusicPlan Music);

public sealed record VisualSource(
    VisualSourceKind Kind,
    Guid? PropertyMediaId,
    Guid? GeneratedClipId,
    Guid? FallbackPropertyMediaId,
    string? GenerationInstruction);

public sealed record TransitionPlan(TransitionKind Type, int DurationMs);

public sealed record MotionPlan(
    MotionKind Type,
    NormalizedRect StartViewport,
    NormalizedRect EndViewport,
    MotionEasing Easing);

public sealed record TextOverlay(
    string Id,
    string Text,
    string GroundingKey,
    int StartOffsetMs,
    int DurationMs,
    OverlayAnchor Anchor,
    NormalizedRect Box,
    TextOverlayStyle StyleToken);

public sealed record LogoOverlay(
    string AssetId,
    int StartOffsetMs,
    int DurationMs,
    NormalizedRect Box,
    decimal Opacity);

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

public sealed record VideoProductionSpecification(
    string SchemaVersion,
    Guid PropertyId,
    Guid PropertyStoryId,
    int PropertyStoryVersion,
    [property: JsonPropertyName("requestedDurationSeconds")] RequestedDuration RequestedDuration,
    VideoAspectRatio AspectRatio,
    VideoOutputProfile Output,
    NormalizedRect SafeZone,
    IReadOnlyList<FactBinding> FactBindings,
    VideoBrandPlan Brand,
    GroundedText CallToAction,
    AudioPlan Audio,
    IReadOnlyList<VideoScene> Scenes);
