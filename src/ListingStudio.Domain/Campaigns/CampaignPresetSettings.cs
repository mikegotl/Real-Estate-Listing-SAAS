using System.Text.Json;
using System.Text.Json.Nodes;

namespace ListingStudio.Domain.Campaigns;

/// <summary>
/// The style choices on the Campaign tab that a user can save under a name and apply to any property. It never holds
/// listing facts (price reduction, open house time), consents, a headshot, or anything that belongs to one property.
/// </summary>
public sealed record CampaignPresetSettings(
    CampaignOutputSelection Outputs,
    CampaignMarketingTemplate MarketingTemplate,
    CampaignVideoElement Elements,
    CampaignSoundtrack Soundtrack,
    CampaignCaptionStyle CaptionStyle,
    CampaignPace Pace,
    CampaignVoiceoverMode Voiceover,
    CampaignNarrationRegion NarrationRegion,
    CampaignLanguage SpokenLanguage,
    CampaignLanguage CaptionLanguage,
    CampaignVoiceAccent VoiceAccent,
    bool LocalizeAgentCard)
{
    public const int CurrentSchemaVersion = 1;

    public const int MaximumJsonLength = 4_000;

    private const CampaignOutputSelection SharedVertical = CampaignOutputSelection.TikTok
        | CampaignOutputSelection.InstagramReel
        | CampaignOutputSelection.YouTubeShort;

    /// <summary>The Campaign tab's own starting choices, also used for any saved value that can no longer be read.</summary>
    public static CampaignPresetSettings Default { get; } = new(
        CampaignOutputSelectionPolicy.Recommended,
        CampaignCreativeOptions.SocialDefault.MarketingTemplate,
        CampaignCreativeOptions.SocialDefault.Elements,
        CampaignCreativeOptions.SocialDefault.Soundtrack,
        CampaignCreativeOptions.SocialDefault.CaptionStyle,
        CampaignCreativeOptions.SocialDefault.Pace,
        CampaignCreativeOptions.SocialDefault.Voiceover,
        CampaignNarrationPreferences.Default.Region,
        CampaignNarrationPreferences.Default.SpokenLanguage,
        CampaignNarrationPreferences.Default.CaptionLanguage,
        CampaignNarrationPreferences.Default.VoiceAccent,
        CampaignNarrationPreferences.Default.LocalizeAgentCard);

    /// <summary>
    /// Brings the choices in line with what the Campaign tab offers today: one shared vertical video, no hidden
    /// kitchen turnaround, and a music checkbox that agrees with the soundtrack.
    /// </summary>
    public CampaignPresetSettings Normalize()
    {
        var outputs = Outputs & ~CampaignOutputSelection.LegacyStandard;
        if ((outputs & SharedVertical) != 0)
        {
            outputs = (outputs & ~SharedVertical) | CampaignOutputSelection.TikTok;
        }

        var elements = Elements & ~CampaignVideoElement.KitchenIslandOrbit;
        elements = Soundtrack is CampaignSoundtrack.None or CampaignSoundtrack.AddInApp
            ? elements & ~CampaignVideoElement.BackgroundMusic
            : elements | CampaignVideoElement.BackgroundMusic;
        return this with { Outputs = outputs, Elements = elements };
    }

    public void Validate()
    {
        if ((Outputs & ~CampaignOutputSelectionPolicy.All) != 0
            || (Elements & ~CampaignCreativeOptions.AllElements) != 0
            || !Enum.IsDefined(MarketingTemplate)
            || !Enum.IsDefined(Soundtrack)
            || !Enum.IsDefined(CaptionStyle)
            || !Enum.IsDefined(Pace)
            || !Enum.IsDefined(Voiceover)
            || !Enum.IsDefined(NarrationRegion)
            || !Enum.IsDefined(SpokenLanguage)
            || !Enum.IsDefined(CaptionLanguage)
            || !Enum.IsDefined(VoiceAccent))
        {
            throw new ArgumentOutOfRangeException(nameof(Outputs), "The saved settings contain an unsupported choice.");
        }
    }

    /// <summary>Stores every choice by name, so a renamed or removed option is noticed instead of misread.</summary>
    public string ToJson()
    {
        Validate();
        var json = new JsonObject
        {
            ["outputs"] = Names(Outputs),
            ["template"] = MarketingTemplate.ToString(),
            ["elements"] = Names(Elements),
            ["soundtrack"] = Soundtrack.ToString(),
            ["captionStyle"] = CaptionStyle.ToString(),
            ["pace"] = Pace.ToString(),
            ["voiceover"] = Voiceover.ToString(),
            ["narrationRegion"] = NarrationRegion.ToString(),
            ["spokenLanguage"] = SpokenLanguage.ToString(),
            ["captionLanguage"] = CaptionLanguage.ToString(),
            ["voiceAccent"] = VoiceAccent.ToString(),
            ["localizeAgentCard"] = LocalizeAgentCard,
        };
        return json.ToJsonString();
    }

    /// <summary>
    /// Reads saved choices. A value this version of the app no longer knows is replaced by the default and named in
    /// <see cref="CampaignPresetSettingsRead.SkippedValues"/>; a missing field (a choice added after the set was saved)
    /// quietly takes the default.
    /// </summary>
    public static CampaignPresetSettingsRead FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject
                ?? throw new FormatException("Saved campaign settings must be a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new FormatException("Saved campaign settings could not be read.", exception);
        }

        var skipped = new List<string>();
        var defaults = Default;
        var settings = new CampaignPresetSettings(
            ReadFlags(root, "outputs", defaults.Outputs, skipped),
            ReadEnum(root, "template", defaults.MarketingTemplate, skipped),
            ReadFlags(root, "elements", defaults.Elements, skipped),
            ReadEnum(root, "soundtrack", defaults.Soundtrack, skipped),
            ReadEnum(root, "captionStyle", defaults.CaptionStyle, skipped),
            ReadEnum(root, "pace", defaults.Pace, skipped),
            ReadEnum(root, "voiceover", defaults.Voiceover, skipped),
            ReadEnum(root, "narrationRegion", defaults.NarrationRegion, skipped),
            ReadEnum(root, "spokenLanguage", defaults.SpokenLanguage, skipped),
            ReadEnum(root, "captionLanguage", defaults.CaptionLanguage, skipped),
            ReadEnum(root, "voiceAccent", defaults.VoiceAccent, skipped),
            root["localizeAgentCard"] is JsonValue localize && localize.TryGetValue<bool>(out var value)
                ? value
                : defaults.LocalizeAgentCard);
        return new CampaignPresetSettingsRead(settings.Normalize(), skipped);
    }

    private static JsonArray Names<TFlags>(TFlags value)
        where TFlags : struct, Enum
    {
        var bits = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        var names = new JsonArray();
        foreach (var flag in Enum.GetValues<TFlags>())
        {
            var bit = Convert.ToInt64(flag, System.Globalization.CultureInfo.InvariantCulture);
            if (bit != 0 && (bits & bit) == bit)
            {
                names.Add(flag.ToString());
            }
        }

        return names;
    }

    private static TEnum ReadEnum<TEnum>(JsonObject root, string name, TEnum fallback, List<string> skipped)
        where TEnum : struct, Enum
    {
        if (root[name] is not JsonValue node || !node.TryGetValue<string>(out var text))
        {
            return fallback;
        }

        if (Enum.TryParse<TEnum>(text, ignoreCase: false, out var parsed)
            && Enum.IsDefined(parsed)
            && !int.TryParse(text, out _))
        {
            return parsed;
        }

        skipped.Add(text);
        return fallback;
    }

    private static TFlags ReadFlags<TFlags>(JsonObject root, string name, TFlags fallback, List<string> skipped)
        where TFlags : struct, Enum
    {
        if (root[name] is not JsonArray items)
        {
            return fallback;
        }

        long bits = 0;
        foreach (var item in items)
        {
            if (item is not JsonValue node || !node.TryGetValue<string>(out var text))
            {
                continue;
            }

            if (Enum.TryParse<TFlags>(text, ignoreCase: false, out var parsed)
                && Enum.IsDefined(parsed)
                && Convert.ToInt64(parsed, System.Globalization.CultureInfo.InvariantCulture) != 0
                && !int.TryParse(text, out _))
            {
                bits |= Convert.ToInt64(parsed, System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
                skipped.Add(text);
            }
        }

        return (TFlags)Enum.ToObject(typeof(TFlags), bits);
    }
}

public sealed record CampaignPresetSettingsRead(
    CampaignPresetSettings Settings,
    IReadOnlyList<string> SkippedValues);
