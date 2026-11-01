using ListingStudio.Domain.Campaigns;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class CaptionStyleDefaultsTests
{
    [Fact]
    public void NewSocialCampaignsAndEveryTemplateStartWithNeonNight()
    {
        Assert.Equal(CampaignCaptionStyle.NeonNight, CampaignCaptionStyles.Default);
        Assert.Equal(CampaignCaptionStyle.NeonNight, CampaignCreativeOptions.SocialDefault.CaptionStyle);
        Assert.Equal(CampaignCaptionStyle.Classic, CampaignCreativeOptions.Legacy.CaptionStyle);
        Assert.Equal(8, CampaignCaptionStyles.Picker.Count);
        Assert.DoesNotContain(CampaignCaptionStyle.Classic, CampaignCaptionStyles.Picker);
        Assert.All(CampaignCaptionStyles.Picker, style => Assert.True(CampaignCaptionStyles.IsBold(style)));
        Assert.False(CampaignCaptionStyles.IsBold(CampaignCaptionStyle.Classic));
    }

    [Theory]
    [InlineData(CampaignCaptionStyle.Pop)]
    [InlineData(CampaignCaptionStyle.BrandBold)]
    [InlineData(CampaignCaptionStyle.Marker)]
    public void SavedSettingsKeepTheChosenLookByName(CampaignCaptionStyle style)
    {
        var settings = CampaignPresetSettings.Default with { CaptionStyle = style };
        var read = CampaignPresetSettings.FromJson(settings.ToJson());
        Assert.Equal(style, read.Settings.CaptionStyle);
        Assert.Empty(read.SkippedValues);
    }
}
