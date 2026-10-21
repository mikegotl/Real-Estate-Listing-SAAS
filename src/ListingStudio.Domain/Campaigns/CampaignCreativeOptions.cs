namespace ListingStudio.Domain.Campaigns;

[Flags]
public enum CampaignVideoElement
{
    None = 0,
    OpeningTitle = 1 << 0,
    PropertyFacts = 1 << 1,
    NarrationCaptions = 1 << 2,
    BackgroundMusic = 1 << 3,
    NeighborhoodHighlights = 1 << 4,
    WalkthroughMotion = 1 << 5,
    KitchenIslandOrbit = 1 << 6,
    AgentPortrait = 1 << 7,
    ClosingCallToAction = 1 << 8,
}

public enum CampaignSoundtrack
{
    None,
    AutoByHomeEra,
    HeritageWarm,
    ModernAmbient,
    UpbeatLifestyle,
}

public enum CampaignCaptionStyle
{
    Classic,
    Pop,
}

public sealed record CampaignCreativeOptions(
    CampaignVideoElement Elements,
    CampaignSoundtrack Soundtrack,
    CampaignCaptionStyle CaptionStyle,
    Guid? AgentPortraitId = null)
{
    public static CampaignCreativeOptions Legacy { get; } = new(
        CampaignVideoElement.OpeningTitle
        | CampaignVideoElement.PropertyFacts
        | CampaignVideoElement.NeighborhoodHighlights
        | CampaignVideoElement.WalkthroughMotion
        | CampaignVideoElement.ClosingCallToAction,
        CampaignSoundtrack.None,
        CampaignCaptionStyle.Classic);

    public static CampaignCreativeOptions SocialDefault { get; } = new(
        Legacy.Elements
        | CampaignVideoElement.NarrationCaptions
        | CampaignVideoElement.BackgroundMusic,
        CampaignSoundtrack.AutoByHomeEra,
        CampaignCaptionStyle.Pop);

    public static CampaignVideoElement AllElements { get; } = Enum.GetValues<CampaignVideoElement>()
        .Aggregate(CampaignVideoElement.None, (all, item) => all | item);

    public void Validate()
    {
        if (Elements == CampaignVideoElement.None || (Elements & ~AllElements) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Elements), "Choose at least one supported video element.");
        }

        if (!Enum.IsDefined(Soundtrack) || !Enum.IsDefined(CaptionStyle))
        {
            throw new ArgumentOutOfRangeException(nameof(Soundtrack));
        }

        var hasMusic = Elements.HasFlag(CampaignVideoElement.BackgroundMusic);
        if (hasMusic == (Soundtrack == CampaignSoundtrack.None))
        {
            throw new ArgumentException("The music checkbox and soundtrack choice must agree.");
        }

        var hasPortrait = Elements.HasFlag(CampaignVideoElement.AgentPortrait);
        if (hasPortrait != AgentPortraitId.HasValue || AgentPortraitId == Guid.Empty)
        {
            throw new ArgumentException("An agent portrait must be uploaded before it can be included.");
        }

        if (Elements.HasFlag(CampaignVideoElement.KitchenIslandOrbit)
            && !Elements.HasFlag(CampaignVideoElement.WalkthroughMotion))
        {
            throw new ArgumentException("Kitchen orbit motion requires walk-through motion.");
        }
    }
}
