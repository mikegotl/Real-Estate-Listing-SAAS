using ListingStudio.Application.Campaigns;
using ListingStudio.Domain.Campaigns;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class CampaignSettingsPresetTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private static readonly CampaignPresetContext Everything = new(
        LongFormAvailable: true,
        HasPortrait: true,
        ProfileIncludesPresenter: false);

    private static CampaignPresetSettings Styled() => new(
        CampaignOutputSelection.TikTok | CampaignOutputSelection.FullNarratedTour | CampaignOutputSelection.FacebookStillPost,
        CampaignMarketingTemplate.JustListed,
        CampaignVideoElement.OpeningTitle | CampaignVideoElement.PropertyFacts | CampaignVideoElement.AgentContact,
        CampaignSoundtrack.AddInApp,
        CampaignCaptionStyle.Classic,
        CampaignPace.Cinematic,
        CampaignVoiceoverMode.None,
        CampaignNarrationRegion.Canada,
        CampaignLanguage.French,
        CampaignLanguage.English,
        CampaignVoiceAccent.FrenchCanadian,
        LocalizeAgentCard: false);

    [Fact]
    public void SettingsRoundTripThroughJson()
    {
        var settings = Styled();

        var read = CampaignPresetSettings.FromJson(settings.ToJson());

        Assert.Equal(settings.Normalize(), read.Settings);
        Assert.Empty(read.SkippedValues);
    }

    [Fact]
    public void JsonStoresChoicesByName()
    {
        var json = Styled().ToJson();

        Assert.Contains("\"JustListed\"", json, StringComparison.Ordinal);
        Assert.Contains("\"FullNarratedTour\"", json, StringComparison.Ordinal);
        Assert.Contains("\"FrenchCanadian\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownSavedValuesFallBackAndAreNamed()
    {
        const string json = """
            {"outputs":["TikTok","HoloDeck"],"template":"SpaceLaunch","elements":["OpeningTitle"],
             "soundtrack":"UpbeatLifestyle","voiceAccent":"Martian","localizeAgentCard":true}
            """;

        var read = CampaignPresetSettings.FromJson(json);

        Assert.Equal(CampaignOutputSelection.TikTok, read.Settings.Outputs);
        Assert.Equal(CampaignPresetSettings.Default.MarketingTemplate, read.Settings.MarketingTemplate);
        Assert.Equal(CampaignPresetSettings.Default.VoiceAccent, read.Settings.VoiceAccent);
        Assert.Equal(["HoloDeck", "SpaceLaunch", "Martian"], read.SkippedValues);
    }

    [Fact]
    public void MissingFieldsTakeTheDefaultQuietly()
    {
        var read = CampaignPresetSettings.FromJson("""{"template":"OpenHouse"}""");

        Assert.Equal(CampaignMarketingTemplate.OpenHouse, read.Settings.MarketingTemplate);
        Assert.Equal(CampaignPresetSettings.Default.Pace, read.Settings.Pace);
        Assert.Empty(read.SkippedValues);
    }

    [Fact]
    public void NumericEnumValuesAreNotTrusted()
    {
        var read = CampaignPresetSettings.FromJson("""{"template":"3","outputs":["2"]}""");

        Assert.Equal(CampaignPresetSettings.Default.MarketingTemplate, read.Settings.MarketingTemplate);
        Assert.Equal(CampaignOutputSelection.None, read.Settings.Outputs);
        Assert.Equal(["2", "3"], read.SkippedValues.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void MalformedJsonIsAFormatError()
    {
        Assert.Throws<FormatException>(() => CampaignPresetSettings.FromJson("not json"));
        Assert.Throws<FormatException>(() => CampaignPresetSettings.FromJson("[1]"));
    }

    [Fact]
    public void NormalizeMapsOldVerticalOutputsAndKeepsMusicConsistent()
    {
        var settings = Styled() with
        {
            Outputs = CampaignOutputSelection.InstagramReel | CampaignOutputSelection.YouTubeShort
                | CampaignOutputSelection.LegacyStandard,
            Elements = CampaignVideoElement.BackgroundMusic | CampaignVideoElement.KitchenIslandOrbit
                | CampaignVideoElement.WalkthroughMotion,
            Soundtrack = CampaignSoundtrack.None,
        };

        var normalized = settings.Normalize();

        Assert.Equal(CampaignOutputSelection.TikTok, normalized.Outputs);
        Assert.Equal(CampaignVideoElement.WalkthroughMotion, normalized.Elements);
        Assert.True((settings with { Soundtrack = CampaignSoundtrack.HeritageWarm }).Normalize()
            .Elements.HasFlag(CampaignVideoElement.BackgroundMusic));
    }

    [Fact]
    public void ApplyingEverythingThatFitsAddsNoNotes()
    {
        var application = CampaignPresetApplier.Apply(Styled(), [], Everything);

        Assert.Equal(Styled().Normalize(), application.Settings);
        Assert.Empty(application.Notes);
        Assert.False(application.AgentAvatarConsentConfirmed);
    }

    [Fact]
    public void FullTourIsLeftOffWithoutALongScript()
    {
        var application = CampaignPresetApplier.Apply(Styled(), [], Everything with { LongFormAvailable = false });

        Assert.False(application.Settings.Outputs.HasFlag(CampaignOutputSelection.FullNarratedTour));
        Assert.True(application.Settings.Outputs.HasFlag(CampaignOutputSelection.TikTok));
        Assert.Contains(application.Notes, note => note.StartsWith("Full narrated tour was left off", StringComparison.Ordinal));
    }

    [Fact]
    public void NoDeliverablesLeftAsksForOne()
    {
        var settings = Styled() with { Outputs = CampaignOutputSelection.FullNarratedTour };

        var application = CampaignPresetApplier.Apply(settings, [], Everything with { LongFormAvailable = false });

        Assert.Equal(CampaignOutputSelection.None, application.Settings.Outputs);
        Assert.Contains("Choose at least one deliverable before generating.", application.Notes);
    }

    [Fact]
    public void AgentCardIsLeftOffWithoutAHeadshot()
    {
        var settings = Styled() with { Elements = Styled().Elements | CampaignVideoElement.AgentAvatar };

        var application = CampaignPresetApplier.Apply(settings, [], Everything with { HasPortrait = false });

        Assert.False(application.Settings.Elements.HasFlag(CampaignVideoElement.AgentAvatar));
        Assert.False(application.Settings.Elements.HasFlag(CampaignVideoElement.AgentPortrait));
        Assert.Contains("Your agent photo was left off because no profile headshot is uploaded.", application.Notes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void PresenterConsentComesOnlyFromTheProfile(bool profileIncludesPresenter, bool consented)
    {
        var settings = Styled() with
        {
            Elements = Styled().Elements | CampaignVideoElement.AgentAvatar | CampaignVideoElement.AgentPortrait,
        };

        var application = CampaignPresetApplier.Apply(
            settings,
            [],
            Everything with { ProfileIncludesPresenter = profileIncludesPresenter });

        Assert.True(application.Settings.Elements.HasFlag(CampaignVideoElement.AgentAvatar));
        Assert.False(application.Settings.Elements.HasFlag(CampaignVideoElement.AgentPortrait));
        Assert.Equal(consented, application.AgentAvatarConsentConfirmed);
        Assert.Equal(!consented, application.Notes.Contains("Tick the AI presenter consent before generating."));
    }

    [Theory]
    [InlineData(CampaignMarketingTemplate.PriceDrop, "Enter this listing's price reduction and confirm it before generating.")]
    [InlineData(CampaignMarketingTemplate.OpenHouse, "Enter this listing's open house day and time and confirm them before generating.")]
    public void ListingFactTemplatesAskForThisListingsFacts(CampaignMarketingTemplate template, string note)
    {
        var application = CampaignPresetApplier.Apply(Styled() with { MarketingTemplate = template }, [], Everything);

        Assert.Equal(template, application.Settings.MarketingTemplate);
        Assert.Contains(note, application.Notes);
    }

    [Fact]
    public void SkippedValuesAreReported()
    {
        var application = CampaignPresetApplier.Apply(Styled(), ["HoloDeck"], Everything);

        Assert.Contains("Skipped saved choices this version no longer offers: HoloDeck.", application.Notes);
    }

    [Fact]
    public void PresetNamesAreCleanedAndCompareCaseInsensitively()
    {
        var preset = CampaignSettingsPreset.Create(Guid.NewGuid(), "user", "  My   TikTok style ", Styled(), Now);

        Assert.Equal("My TikTok style", preset.Name);
        Assert.Equal("MY TIKTOK STYLE", preset.NormalizedName);
        Assert.Equal(CampaignPresetSettings.CurrentSchemaVersion, preset.SchemaVersion);
        Assert.False(preset.IsShared);
        Assert.False(preset.IsDefault);
        Assert.Equal(Styled().Normalize(), preset.ReadSettings().Settings);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\u0007name")]
    public void InvalidPresetNamesAreRejected(string name)
    {
        Assert.Throws<ArgumentException>(() =>
            CampaignSettingsPreset.Create(Guid.NewGuid(), "user", name, Styled(), Now));
    }

    [Fact]
    public void OverlongPresetNamesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => CampaignSettingsPreset.Create(
            Guid.NewGuid(),
            "user",
            new string('a', CampaignSettingsPreset.MaximumNameLength + 1),
            Styled(),
            Now));
    }
}
