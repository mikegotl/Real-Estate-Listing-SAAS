namespace ListingStudio.Domain.Stories;

public sealed record PropertyStoryContent(
    string CampaignTitle,
    string OpeningHook,
    string PropertyNarrative,
    IReadOnlyList<string> Highlights,
    string VoiceoverScript,
    string ClosingCta,
    string SocialCaptionLong,
    string SocialCaptionShort);
