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
    AgentAvatar = 1 << 9,
    /// <summary>The agent's name and phone along the bottom of short-form videos.</summary>
    AgentContact = 1 << 10,
}

public enum CampaignSoundtrack
{
    None,
    AutoByHomeEra,
    HeritageWarm,
    ModernAmbient,
    UpbeatLifestyle,
    /// <summary>No music in the file; the agent adds a sound in the TikTok or Instagram app.</summary>
    AddInApp,
    /// <summary>Piano and strings for high-end listings.</summary>
    LuxuryCinematic,
}

/// <summary>
/// The look of on-screen text in campaign videos. Stored by name; append only. Pop is the original social look
/// (picker option A); the named looks after it each pair a bundled font with their own colors (options B to H).
/// </summary>
public enum CampaignCaptionStyle
{
    Classic,
    Pop,
    BoldOutline,
    CandyPop,
    SunsetPills,
    Comic,
    NeonNight,
    Marker,
    BrandBold,
}

public static class CampaignCaptionStyles
{
    /// <summary>The look new social campaigns start with.</summary>
    public const CampaignCaptionStyle Default = CampaignCaptionStyle.NeonNight;

    /// <summary>The looks the Campaign tab offers, in picker order A to H.</summary>
    public static IReadOnlyList<CampaignCaptionStyle> Picker { get; } =
    [
        CampaignCaptionStyle.Pop,
        CampaignCaptionStyle.BoldOutline,
        CampaignCaptionStyle.CandyPop,
        CampaignCaptionStyle.SunsetPills,
        CampaignCaptionStyle.Comic,
        CampaignCaptionStyle.NeonNight,
        CampaignCaptionStyle.Marker,
        CampaignCaptionStyle.BrandBold,
    ];

    /// <summary>True for the looks that show captions large and bold (everything except Classic).</summary>
    public static bool IsBold(CampaignCaptionStyle style) => style != CampaignCaptionStyle.Classic;
}

public enum CampaignMarketingTemplate
{
    BalancedShowcase,
    PriceDrop,
    QuickTour,
    FeatureSpotlight,
    AgentShowcase,
    JustListed,
    OpenHouse,
}

/// <summary>How short-form (TikTok, Reels, Shorts) videos move.</summary>
public enum CampaignPace
{
    /// <summary>A cut on the beat every one to two seconds, every shot moving.</summary>
    Fast,

    /// <summary>Longer gliding shots joined by walk-through clips.</summary>
    Cinematic,
}

/// <summary>What short-form videos say out loud.</summary>
public enum CampaignVoiceoverMode
{
    /// <summary>A short voiceover written for the cut from the approved story.</summary>
    ShortHook,

    /// <summary>Music and on-screen text only.</summary>
    None,

    /// <summary>The full tour voiceover, in a 60-second vertical video.</summary>
    Full,
}

public sealed record CampaignCreativeOptions(
    CampaignVideoElement Elements,
    CampaignSoundtrack Soundtrack,
    CampaignCaptionStyle CaptionStyle,
    Guid? AgentPortraitId = null,
    CampaignMarketingTemplate MarketingTemplate = CampaignMarketingTemplate.BalancedShowcase,
    decimal? PriceReductionAmount = null,
    bool PriceReductionVerified = false,
    bool AgentAvatarConsentConfirmed = false,
    CampaignPace Pace = CampaignPace.Fast,
    CampaignVoiceoverMode Voiceover = CampaignVoiceoverMode.ShortHook,
    string? OpenHouseText = null,
    bool OpenHouseVerified = false,
    NativePresenterScript? PresenterScript = null)
{
    public const int MaximumOpenHouseTextLength = 40;

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
        | CampaignVideoElement.BackgroundMusic
        | CampaignVideoElement.AgentContact,
        CampaignSoundtrack.AutoByHomeEra,
        CampaignCaptionStyles.Default,
        MarketingTemplate: CampaignMarketingTemplate.BalancedShowcase);

    /// <summary>
    /// The elements a campaign asks for once its music choice is applied: background music is on exactly when a track
    /// plays.
    /// </summary>
    public static CampaignVideoElement WithSoundtrack(CampaignVideoElement elements, CampaignSoundtrack soundtrack) =>
        soundtrack is CampaignSoundtrack.None or CampaignSoundtrack.AddInApp
            ? elements & ~CampaignVideoElement.BackgroundMusic
            : elements | CampaignVideoElement.BackgroundMusic;

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

        if (!Enum.IsDefined(Pace) || !Enum.IsDefined(Voiceover))
        {
            throw new ArgumentOutOfRangeException(nameof(Pace));
        }

        if (MarketingTemplate == CampaignMarketingTemplate.OpenHouse)
        {
            if (string.IsNullOrWhiteSpace(OpenHouseText)
                || OpenHouseText.Trim().Length > MaximumOpenHouseTextLength
                || OpenHouseText.Any(char.IsControl))
            {
                throw new ArgumentException(
                    $"Enter the open house day and time in up to {MaximumOpenHouseTextLength} characters.",
                    nameof(OpenHouseText));
            }

            if (!OpenHouseVerified)
            {
                throw new ArgumentException(
                    "Confirm that the open house day and time are accurate.",
                    nameof(OpenHouseVerified));
            }
        }
        else if (OpenHouseText is not null || OpenHouseVerified)
        {
            throw new ArgumentException(
                "Open house details can be used only with the Open House template.",
                nameof(OpenHouseText));
        }

        PresenterScript?.Validate();

        var hasMusic = Elements.HasFlag(CampaignVideoElement.BackgroundMusic);
        if (hasMusic == (Soundtrack is CampaignSoundtrack.None or CampaignSoundtrack.AddInApp))
        {
            throw new ArgumentException("The music checkbox and soundtrack choice must agree.");
        }

        var hasPortrait = Elements.HasFlag(CampaignVideoElement.AgentPortrait)
            || Elements.HasFlag(CampaignVideoElement.AgentAvatar);
        if (hasPortrait != AgentPortraitId.HasValue || AgentPortraitId == Guid.Empty)
        {
            throw new ArgumentException("An agent portrait must be uploaded before it can be included.");
        }

        var hasAvatar = Elements.HasFlag(CampaignVideoElement.AgentAvatar);
        if (hasAvatar != AgentAvatarConsentConfirmed)
        {
            throw new ArgumentException(
                "Confirm that the portrait is yours and that you consent to its use as an AI presenter.",
                nameof(AgentAvatarConsentConfirmed));
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
        bool priceReductionVerified = false,
        string? openHouseText = null,
        bool openHouseVerified = false)
    {
        var common = CampaignVideoElement.AgentContact
            | CampaignVideoElement.OpeningTitle
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
                CampaignCaptionStyles.Default,
                MarketingTemplate: template,
                PriceReductionAmount: priceReductionAmount,
                PriceReductionVerified: priceReductionVerified),
            CampaignMarketingTemplate.QuickTour => new CampaignCreativeOptions(
                common,
                CampaignSoundtrack.UpbeatLifestyle,
                CampaignCaptionStyles.Default,
                MarketingTemplate: template),
            CampaignMarketingTemplate.FeatureSpotlight => new CampaignCreativeOptions(
                common | CampaignVideoElement.PropertyFacts | CampaignVideoElement.NeighborhoodHighlights,
                CampaignSoundtrack.AutoByHomeEra,
                CampaignCaptionStyles.Default,
                MarketingTemplate: template),
            CampaignMarketingTemplate.JustListed => new CampaignCreativeOptions(
                common | CampaignVideoElement.PropertyFacts,
                CampaignSoundtrack.UpbeatLifestyle,
                CampaignCaptionStyles.Default,
                MarketingTemplate: template),
            CampaignMarketingTemplate.OpenHouse => new CampaignCreativeOptions(
                common | CampaignVideoElement.PropertyFacts,
                CampaignSoundtrack.UpbeatLifestyle,
                CampaignCaptionStyles.Default,
                MarketingTemplate: template,
                OpenHouseText: openHouseText,
                OpenHouseVerified: openHouseVerified),
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

/// <summary>
/// The exact words the Kling 4 native presenter speaks: an on-camera opening, a voiceover over the AI-generated tour,
/// and an on-camera closing. Thirty seconds holds about 75 spoken words, so the whole script is capped there.
/// </summary>
public sealed record NativePresenterScript(string Opening, string Tour, string Closing)
{
    public const int MaximumWords = 75;
    public const int MaximumOpeningLength = 300;
    public const int MaximumTourLength = 600;
    public const int MaximumClosingLength = 300;

    public int WordCount => CountWords(Opening) + CountWords(Tour) + CountWords(Closing);

    public static int CountWords(string? text) =>
        string.IsNullOrWhiteSpace(text) ? 0 : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>The same script with runs of whitespace collapsed, as it is stored and sent.</summary>
    public NativePresenterScript Normalized() => new(Clean(Opening), Clean(Tour), Clean(Closing));

    public void Validate()
    {
        Check(Opening, MaximumOpeningLength, nameof(Opening), "opening");
        Check(Tour, MaximumTourLength, nameof(Tour), "tour voiceover");
        Check(Closing, MaximumClosingLength, nameof(Closing), "closing");
        if (WordCount > MaximumWords)
        {
            throw new ArgumentException(
                $"The presenter script is {WordCount} words; keep it to {MaximumWords} so it fits 30 seconds.",
                nameof(Tour));
        }
    }

    private static void Check(string? value, int maximumLength, string field, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Write the presenter's {label}.", field);
        }

        if (value.Trim().Length > maximumLength || value.Any(character => char.IsControl(character) && character is not ('\n' or '\r')))
        {
            throw new ArgumentException($"Keep the presenter's {label} to {maximumLength} characters of plain text.", field);
        }

        // Quotation marks would close the quoted line in the provider prompt.
        if (value.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException($"Remove double quotation marks from the presenter's {label}.", field);
        }
    }

    private static string Clean(string? value) =>
        string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
