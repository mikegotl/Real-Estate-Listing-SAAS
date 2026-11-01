using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Videos;

namespace ListingStudio.Video.Rendering;

/// <summary>How one kind of on-screen text is drawn in a caption look.</summary>
/// <param name="TextColor">FFmpeg color, e.g. <c>0xFFE14D</c> or <c>white</c>.</param>
/// <param name="BoxColor">Pill color behind the text, or null for no pill.</param>
/// <param name="BoxOpacity">Pill opacity (0 to 1).</param>
/// <param name="BorderWidth">Text outline width in pixels at 1080 px wide (0 for none).</param>
/// <param name="BorderColor">Outline color.</param>
/// <param name="ShadowOffset">Drop shadow offset in pixels at 1080 px wide (0 for none).</param>
/// <param name="ShadowColor">Drop shadow color.</param>
/// <param name="SizeScale">Multiplier on the branding template's font size for this kind of text.</param>
public sealed record CaptionLookText(
    string TextColor,
    string? BoxColor,
    decimal BoxOpacity,
    int BorderWidth,
    string BorderColor,
    int ShadowOffset,
    string ShadowColor,
    decimal SizeScale);

/// <summary>
/// One picker look: a bundled font plus how titles (price), facts (beds, baths, contact) and captions are drawn.
/// Captions alternate between <see cref="Caption"/> and <see cref="CaptionAccent"/> chunk by chunk.
/// </summary>
public sealed record CaptionLook(
    CampaignCaptionStyle Style,
    string FontFile,
    decimal CharacterWidth,
    CaptionLookText Title,
    CaptionLookText Fact,
    CaptionLookText Caption,
    CaptionLookText CaptionAccent,
    bool Uppercase = false)
{
    public string FontPath => Path.Combine(CaptionLooks.FontDirectory, FontFile);

    public CaptionLookText For(TextOverlayStyle style) => style switch
    {
        TextOverlayStyle.HookTitle or TextOverlayStyle.OpeningTitle or TextOverlayStyle.ClosingCta => Title,
        TextOverlayStyle.PopCaption or TextOverlayStyle.LowerThird => Caption,
        _ => Fact,
    };
}

/// <summary>
/// The caption looks B to H from the Campaign tab picker. Pop (A) and Classic keep the configured branding template.
/// Values match the previews the owner approved on 2026-11-01.
/// </summary>
public static class CaptionLooks
{
    public static string FontDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "Fonts");

    private static CaptionLookText Outline(string color, int border, string borderColor, decimal scale, int shadow = 0, string shadowColor = "black@0.5") =>
        new(color, null, 0, border, borderColor, shadow, shadowColor, scale);

    private static CaptionLookText Pill(string color, string box, decimal opacity, decimal scale, int border = 0, string borderColor = "black") =>
        new(color, box, opacity, border, borderColor, 0, "black", scale);

    private static readonly CaptionLook[] All =
    [
        new(CampaignCaptionStyle.BoldOutline, "Anton-Regular.ttf", 0.42m,
            Outline("0xFFE14D", 10, "black", 1.55m, shadow: 8),
            Outline("white", 7, "black", 1.2m),
            Outline("white", 8, "black", 1.6m),
            Outline("0xFFE14D", 8, "black", 1.6m), Uppercase: true),
        new(CampaignCaptionStyle.CandyPop, "LuckiestGuy-Regular.ttf", 0.6m,
            Pill("0xFFE600", "0xFF2E88", 0.95m, 1.25m, border: 8, borderColor: "0x7A0040"),
            Pill("white", "0x7B2FF7", 0.95m, 0.97m),
            Outline("white", 7, "0x7A0040", 1.5m),
            Outline("0xFFE600", 7, "0x7A0040", 1.5m), Uppercase: true),
        new(CampaignCaptionStyle.SunsetPills, "Montserrat-Black.ttf", 0.66m,
            Pill("white", "0xFF5A36", 0.96m, 1.15m),
            Pill("0x2B1B12", "0xFFC43D", 0.96m, 0.92m),
            Pill("white", "0xFF5A36", 0.92m, 1.17m),
            Pill("0x2B1B12", "0xFFC43D", 0.95m, 1.17m)),
        new(CampaignCaptionStyle.Comic, "Bangers-Regular.ttf", 0.44m,
            Outline("white", 6, "black", 1.75m, shadow: 10, shadowColor: "0xE63946"),
            Outline("0x4CC9F0", 6, "black", 1.33m),
            Outline("white", 6, "black", 1.65m, shadow: 6, shadowColor: "0xE63946"),
            Outline("0xFFD60A", 7, "black", 1.65m), Uppercase: true),
        new(CampaignCaptionStyle.NeonNight, "BebasNeue-Regular.ttf", 0.4m,
            Outline("0x00F5D4", 4, "0x003B36", 1.65m),
            Pill("white", "0x111122", 0.8m, 1.25m),
            Pill("white", "0x111122", 0.78m, 1.7m),
            Outline("0xFF4FD8", 4, "0x3A0030", 1.7m), Uppercase: true),
        new(CampaignCaptionStyle.Marker, "PermanentMarker-Regular.ttf", 0.66m,
            Outline("0xFFE14D", 6, "black@0.85", 1.33m),
            Outline("white", 5, "black@0.85", 1.0m),
            Outline("white", 6, "black@0.85", 1.4m),
            Outline("0xFFE14D", 6, "black@0.85", 1.4m)),
        new(CampaignCaptionStyle.BrandBold, "Poppins-ExtraBold.ttf", 0.64m,
            Pill("white", "0x1F3A5F", 0.95m, 1.25m),
            Pill("0x1F3A5F", "0xD4A64A", 0.97m, 0.94m),
            Outline("white", 6, "0x0B1A2E", 1.23m),
            Outline("0xF2C14E", 6, "0x0B1A2E", 1.23m)),
    ];

    public static IReadOnlyList<CaptionLook> Looks => All;

    /// <summary>The look for a picker style, or null for Pop and Classic (the configured branding template).</summary>
    public static CaptionLook? For(CampaignCaptionStyle? style) =>
        style is { } value ? Array.Find(All, look => look.Style == value) : null;
}
