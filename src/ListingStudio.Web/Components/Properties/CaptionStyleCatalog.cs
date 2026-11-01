using ListingStudio.Domain.Campaigns;

namespace ListingStudio.Web.Components.Properties;

/// <summary>A caption look as the picker shows it.</summary>
public sealed record CaptionStyleChoice(CampaignCaptionStyle Style, string Letter, string Name, string Description, string Thumbnail);

/// <summary>The Campaign tab caption looks, A to H, with the thumbnails the video renderer drew for each.</summary>
public static class CaptionStyleCatalog
{
    public static IReadOnlyList<CaptionStyleChoice> Choices { get; } =
    [
        new(CampaignCaptionStyle.Pop, "A", "Original", "Clean sans serif in your brand colors", "/images/caption-styles/pop.webp"),
        new(CampaignCaptionStyle.BoldOutline, "B", "Bold outline", "Classic TikTok: big yellow price, heavy black outline", "/images/caption-styles/bold-outline.webp"),
        new(CampaignCaptionStyle.CandyPop, "C", "Candy pop", "Playful font on hot pink and purple pills", "/images/caption-styles/candy-pop.webp"),
        new(CampaignCaptionStyle.SunsetPills, "D", "Sunset pills", "Bold white on coral and gold blocks", "/images/caption-styles/sunset-pills.webp"),
        new(CampaignCaptionStyle.Comic, "E", "Comic", "Punchy comic lettering with a red shadow", "/images/caption-styles/comic.webp"),
        new(CampaignCaptionStyle.NeonNight, "F", "Neon night", "Tall condensed type in electric teal and pink", "/images/caption-styles/neon-night.webp"),
        new(CampaignCaptionStyle.Marker, "G", "Marker", "Hand-drawn yellow and white", "/images/caption-styles/marker.webp"),
        new(CampaignCaptionStyle.BrandBold, "H", "Brand bold", "Clean and modern in navy and brass", "/images/caption-styles/brand-bold.webp"),
    ];

    /// <summary>The picker choice for a style. Classic (older campaigns only) shows as the original look.</summary>
    public static CaptionStyleChoice For(CampaignCaptionStyle style) =>
        Choices.FirstOrDefault(choice => choice.Style == style) ?? Choices[0];

    public static string Label(CampaignCaptionStyle style) =>
        style == CampaignCaptionStyle.Classic ? "Classic captions" : $"{For(style).Letter} · {For(style).Name}";
}
