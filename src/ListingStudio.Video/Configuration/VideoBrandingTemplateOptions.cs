using ListingStudio.Domain.Videos;

namespace ListingStudio.Video.Configuration;

public sealed class VideoBrandingTemplateOptions
{
    public const string SectionName = "VideoBranding";

    public string FontFilePath { get; set; } = string.Empty;

    public int VideoFadeInMs { get; set; } = 500;

    public int VideoFadeOutMs { get; set; } = 750;

    public TextOverlayTemplateOptions OpeningTitle { get; set; } = new()
    {
        FontSize = 64,
        TextColor = BrandColorToken.Secondary,
        BoxColor = BrandColorToken.Primary,
        BoxOpacity = 0.78m,
        Padding = 24,
    };

    public TextOverlayTemplateOptions PropertyFact { get; set; } = new()
    {
        FontSize = 52,
        TextColor = BrandColorToken.Secondary,
        BoxColor = BrandColorToken.Primary,
        BoxOpacity = 0.72m,
        Padding = 20,
    };

    public TextOverlayTemplateOptions LowerThird { get; set; } = new()
    {
        FontSize = 44,
        TextColor = BrandColorToken.Primary,
        BoxColor = BrandColorToken.Secondary,
        BoxOpacity = 0.84m,
        Padding = 18,
    };

    public TextOverlayTemplateOptions ClosingCta { get; set; } = new()
    {
        FontSize = 56,
        TextColor = BrandColorToken.Secondary,
        BoxColor = BrandColorToken.Primary,
        BoxOpacity = 0.82m,
        Padding = 22,
    };

    public TextOverlayTemplateOptions For(TextOverlayStyle style) => style switch
    {
        TextOverlayStyle.OpeningTitle => OpeningTitle,
        TextOverlayStyle.PropertyFact => PropertyFact,
        TextOverlayStyle.LowerThird => LowerThird,
        TextOverlayStyle.ClosingCta => ClosingCta,
        _ => throw new ArgumentOutOfRangeException(nameof(style)),
    };
}

public sealed class TextOverlayTemplateOptions
{
    public int FontSize { get; set; }

    public BrandColorToken TextColor { get; set; }

    public BrandColorToken BoxColor { get; set; }

    public decimal BoxOpacity { get; set; }

    public int Padding { get; set; }
}

public enum BrandColorToken
{
    Primary,
    Secondary,
}
