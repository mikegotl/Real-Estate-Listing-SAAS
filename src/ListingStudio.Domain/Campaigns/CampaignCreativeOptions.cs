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

public enum CampaignMarketingTemplate
{
    BalancedShowcase,
    PriceDrop,
    QuickTour,
    FeatureSpotlight,
    AgentShowcase,
}

public sealed record CampaignCreativeOptions(
    CampaignVideoElement Elements,
    CampaignSoundtrack Soundtrack,
    CampaignCaptionStyle CaptionStyle,
    Guid? AgentPortraitId = null,
    CampaignMarketingTemplate MarketingTemplate = CampaignMarketingTemplate.BalancedShowcase,
    decimal? PriceReductionAmount = null,
    bool PriceReductionVerified = false)
{
    public static CampaignCreativeOptions Legacy { get; } = new(
        CampaignVideoElement.OpeningTitle
        | CampaignVideoElement.PropertyFacts
        | CampaignVideoElement.NeighborhoodHighlights
        | CampaignVideoElement.WalkthroughMotion
        | CampaignVideoElement.ClosingCallToAction,
        CampaignSoundtrack.None,
        CampaignCaptionStyle.Classic,
        MarketingTemplate: CampaignMarketingTemplate.BalancedShowcase);

    public static CampaignCreativeOptions SocialDefault { get; } = new(
        Legacy.Elements
        | CampaignVideoElement.NarrationCaptions
        | CampaignVideoElement.BackgroundMusic,
        CampaignSoundtrack.AutoByHomeEra,
        CampaignCaptionStyle.Pop,
        MarketingTemplate: CampaignMarketingTemplate.BalancedShowcase);

    public static CampaignVideoElement AllElements { get; } = Enum.GetValues<CampaignVideoElement>()
        .Aggregate(CampaignVideoElement.None, (all, item) => all | item);

    public void Validate()
    {
        if (Elements == CampaignVideoElement.None || (Elements & ~AllElements) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Elements), "Choose at least one supported video element.");
        }

        if (!Enum.IsDefined(Soundtrack))
        {
            throw new ArgumentOutOfRangeException(nameof(Soundtrack));
        }

        if (!Enum.IsDefined(CaptionStyle))
        {
            throw new ArgumentOutOfRangeException(nameof(CaptionStyle));
        }

        if (!Enum.IsDefined(MarketingTemplate))
        {
            throw new ArgumentOutOfRangeException(nameof(MarketingTemplate));
        }

        if (MarketingTemplate == CampaignMarketingTemplate.PriceDrop)
        {
            if (PriceReductionAmount is null or <= 0 or > 100_000_000m)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(PriceReductionAmount),
                    "Enter the verified price-reduction amount.");
            }

            if (!PriceReductionVerified)
            {
                throw new ArgumentException(
                    "Confirm that the price reduction is accurate and approved for marketing.",
                    nameof(PriceReductionVerified));
            }
        }
        else if (PriceReductionAmount.HasValue || PriceReductionVerified)
        {
            throw new ArgumentException(
                "Price-reduction details can be used only with the Price Drop template.",
                nameof(PriceReductionAmount));
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

    public static CampaignCreativeOptions ForTemplate(
        CampaignMarketingTemplate template,
        Guid? agentPortraitId = null,
        decimal? priceReductionAmount = null,
        bool priceReductionVerified = false)
    {
        var common = CampaignVideoElement.OpeningTitle
            | CampaignVideoElement.NarrationCaptions
            | CampaignVideoElement.BackgroundMusic
            | CampaignVideoElement.WalkthroughMotion
            | CampaignVideoElement.ClosingCallToAction;
        var options = template switch
        {
            CampaignMarketingTemplate.BalancedShowcase => SocialDefault with
            {
                MarketingTemplate = template,
            },
            CampaignMarketingTemplate.PriceDrop => new CampaignCreativeOptions(
                common | CampaignVideoElement.PropertyFacts,
                CampaignSoundtrack.UpbeatLifestyle,
                CampaignCaptionStyle.Pop,
                MarketingTemplate: template,
                PriceReductionAmount: priceReductionAmount,
                PriceReductionVerified: priceReductionVerified),
            CampaignMarketingTemplate.QuickTour => new CampaignCreativeOptions(
                common,
                CampaignSoundtrack.UpbeatLifestyle,
                CampaignCaptionStyle.Pop,
                MarketingTemplate: template),
            CampaignMarketingTemplate.FeatureSpotlight => new CampaignCreativeOptions(
                common | CampaignVideoElement.PropertyFacts | CampaignVideoElement.NeighborhoodHighlights,
                CampaignSoundtrack.AutoByHomeEra,
                CampaignCaptionStyle.Pop,
                MarketingTemplate: template),
            CampaignMarketingTemplate.AgentShowcase => new CampaignCreativeOptions(
                common | CampaignVideoElement.PropertyFacts
                    | (agentPortraitId.HasValue ? CampaignVideoElement.AgentPortrait : CampaignVideoElement.None),
                CampaignSoundtrack.AutoByHomeEra,
                CampaignCaptionStyle.Pop,
                agentPortraitId,
                template),
            _ => throw new ArgumentOutOfRangeException(nameof(template)),
        };

        options.Validate();
        return options;
    }
}
