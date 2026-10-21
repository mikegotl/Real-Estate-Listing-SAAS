namespace ListingStudio.Domain.Campaigns;

public enum CampaignNarrationRegion
{
    UnitedStates,
    Canada,
    UnitedKingdom,
    Australia,
    Mexico,
    Spain,
    France,
    Germany,
    Italy,
    Brazil,
}

public enum CampaignLanguage
{
    English,
    Spanish,
    French,
    German,
    Italian,
    Portuguese,
}

public enum CampaignVoiceAccent
{
    Neutral,
    American,
    British,
    Australian,
    Canadian,
    LatinAmerican,
    Castilian,
    French,
    FrenchCanadian,
    German,
    Italian,
    BrazilianPortuguese,
    EuropeanPortuguese,
}

/// <summary>
/// User-selected presentation settings. Region controls wording and pronunciation conventions; it is not a
/// statement about the agent's or viewer's identity.
/// </summary>
public sealed record CampaignNarrationPreferences(
    CampaignNarrationRegion Region,
    CampaignLanguage SpokenLanguage,
    CampaignLanguage CaptionLanguage,
    CampaignVoiceAccent VoiceAccent,
    bool LocalizeAgentCard = true)
{
    public static CampaignNarrationPreferences Default { get; } = new(
        CampaignNarrationRegion.UnitedStates,
        CampaignLanguage.English,
        CampaignLanguage.English,
        CampaignVoiceAccent.American);

    public void Validate()
    {
        if (!Enum.IsDefined(Region)
            || !Enum.IsDefined(SpokenLanguage)
            || !Enum.IsDefined(CaptionLanguage)
            || !Enum.IsDefined(VoiceAccent))
        {
            throw new ArgumentOutOfRangeException(nameof(Region), "Choose supported narration language settings.");
        }
    }

    public string SpokenLanguageCode => SpokenLanguage switch
    {
        CampaignLanguage.English => "en",
        CampaignLanguage.Spanish => "es",
        CampaignLanguage.French => "fr",
        CampaignLanguage.German => "de",
        CampaignLanguage.Italian => "it",
        CampaignLanguage.Portuguese => "pt",
        _ => throw new ArgumentOutOfRangeException(nameof(SpokenLanguage)),
    };

    public string CaptionLanguageCode => CaptionLanguage switch
    {
        CampaignLanguage.English => "en",
        CampaignLanguage.Spanish => "es",
        CampaignLanguage.French => "fr",
        CampaignLanguage.German => "de",
        CampaignLanguage.Italian => "it",
        CampaignLanguage.Portuguese => "pt",
        _ => throw new ArgumentOutOfRangeException(nameof(CaptionLanguage)),
    };
}
