using ListingStudio.Domain.Campaigns;
using ListingStudio.Infrastructure.Campaigns;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class NativePresenterScriptTests
{
    [Fact]
    public void ScriptMustFitThirtySecondsAndHaveEveryPart()
    {
        new NativePresenterScript("Hi, I'm Dana.", "Here is the kitchen.", "Call me today.").Validate();

        var tooLong = new NativePresenterScript("Hi.", string.Join(' ', Enumerable.Repeat("word", 80)), "Bye.");
        var error = Assert.Throws<ArgumentException>(() => tooLong.Validate());
        Assert.Contains("75", error.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new NativePresenterScript("Hi.", " ", "Bye.").Validate());
        Assert.Throws<ArgumentException>(() => new NativePresenterScript("Say \"hi\".", "Tour.", "Bye.").Validate());
    }

    [Fact]
    public void CreativeOptionsValidateTheScript()
    {
        var options = CampaignCreativeOptions.SocialDefault with
        {
            PresenterScript = new NativePresenterScript("Hi.", string.Join(' ', Enumerable.Repeat("word", 80)), "Bye."),
        };

        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void SuggestedScriptWalksTheTourMapInOrderInsideTheWordBudget()
    {
        var frames = new[]
        {
            Frame("Exterior front", "A brick colonial on a quiet street. It has a deep lawn."),
            Frame("Kitchen", null),
            Frame("Primary bedroom", string.Join(' ', Enumerable.Repeat("long", 70)) + "."),
            Frame("Back patio", "A covered patio for summer evenings."),
        };
        var input = new NativePresenterPlanInput(
            Guid.NewGuid(), Guid.NewGuid(), VerticalCampaignGoal.BuyerInterest,
            CampaignMarketingTemplate.BalancedShowcase, null, CampaignSoundtrack.AutoByHomeEra);

        var script = NativePresenterRequestPlanner.SuggestScript("Ocala", "FL", frames, input);

        script.Validate();
        // The state code is written the way it is said, so Kling does not spell out "F L".
        Assert.Equal("Welcome to your next home in Ocala, Florida. Let me show you around.", script.Opening);
        // First sentence of each approved narration, the stop name when there is none, and nothing past the budget.
        Assert.Equal("A brick colonial on a quiet street. Here is the kitchen.", script.Tour);
        Assert.Equal(NativePresenterRequestPlanner.ClosingLine, script.Closing);
    }

    [Fact]
    public void SuggestedScriptClosesWithTheVerifiedOpenHouse()
    {
        var input = new NativePresenterPlanInput(
            Guid.NewGuid(), Guid.NewGuid(), null, CampaignMarketingTemplate.OpenHouse, "Sat 1-3 PM",
            CampaignSoundtrack.AutoByHomeEra);

        var script = NativePresenterRequestPlanner.SuggestScript("Ocala", "FL", [Frame("Kitchen", null)], input);

        Assert.Equal("Join me at the open house, Sat 1-3 PM. Contact me to save your spot.", script.Closing);
    }

    [Fact]
    public void EachVideoLengthHasItsOwnWordBudget()
    {
        Assert.Equal(75, NativePresenterScript.MaximumWordsFor(30));
        Assert.Equal(37, NativePresenterScript.MaximumWordsFor(15));
        var thirtyWords = new NativePresenterScript("Hi.", string.Join(' ', Enumerable.Repeat("word", 40)), "Bye.");

        thirtyWords.Validate(30);
        var error = Assert.Throws<ArgumentException>(() => thirtyWords.Validate(15));
        Assert.Contains("37", error.Message, StringComparison.Ordinal);
        Assert.Contains("15 seconds", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ShortVideosUseFewerKeyframes()
    {
        Assert.Equal(8, Application.Videos.NativePresenterVideoPolicy.MaximumTourFramesFor(30));
        Assert.Equal(4, Application.Videos.NativePresenterVideoPolicy.MaximumTourFramesFor(15));
        Assert.Equal(1, Application.Videos.NativePresenterVideoPolicy.MaximumTourFramesFor(3));
    }

    [Fact]
    public void CreativeOptionsAllowOneScriptPerLibraryVideo()
    {
        var script = new NativePresenterScript("Hi.", "Tour.", "Bye.");
        (CampaignCreativeOptions.SocialDefault with
        {
            FormatPresenterScripts =
            [
                new FormatPresenterScript(VerticalCreativeFormat.PriceFirst, script),
                new FormatPresenterScript(VerticalCreativeFormat.WowFeature, script),
            ],
        }).Validate();

        Assert.Throws<ArgumentException>(() => (CampaignCreativeOptions.SocialDefault with
        {
            FormatPresenterScripts =
            [
                new FormatPresenterScript(VerticalCreativeFormat.PriceFirst, script),
                new FormatPresenterScript(VerticalCreativeFormat.PriceFirst, script),
            ],
        }).Validate());
        Assert.Throws<ArgumentException>(() => (CampaignCreativeOptions.SocialDefault with
        {
            FormatPresenterScripts = [new FormatPresenterScript(VerticalCreativeFormat.LegacySingle, script)],
        }).Validate());
    }

    [Fact]
    public void PriceFirstScriptLeadsWithThePriceAndFacts()
    {
        var input = new NativePresenterPlanInput(
            Guid.NewGuid(), Guid.NewGuid(), null, CampaignMarketingTemplate.BalancedShowcase, null,
            CampaignSoundtrack.AutoByHomeEra, Format: VerticalCreativeFormat.PriceFirst);

        var script = NativePresenterRequestPlanner.SuggestScript(
            new NativePresenterListing("Wildwood", "FL", 400_000m, 3, 2.5m, 2_000),
            [Frame("Kitchen", "An open kitchen with a long island."), Frame("Primary bedroom", null)],
            input);

        script.Validate(30);
        Assert.Equal("This home in Wildwood, Florida is listed at $400,000.", script.Opening);
        Assert.StartsWith("3 bedrooms, 2 and a half bathrooms, 2,000 square feet.", script.Tour, StringComparison.Ordinal);
        Assert.Contains("An open kitchen with a long island.", script.Tour, StringComparison.Ordinal);
    }

    [Fact]
    public void WowFeatureScriptNamesItsOpeningSpaceAndFitsFifteenSeconds()
    {
        var input = new NativePresenterPlanInput(
            Guid.NewGuid(), Guid.NewGuid(), null, CampaignMarketingTemplate.BalancedShowcase, null,
            CampaignSoundtrack.AutoByHomeEra, Format: VerticalCreativeFormat.WowFeature, DurationSeconds: 15);
        var frames = new[]
        {
            Frame("Sunroom", "Walls of glass look over the lake."),
            Frame("Kitchen", "An open kitchen with a long island and room for six stools."),
            Frame("Primary bedroom", "A quiet primary suite with a walk-in closet and soaking tub."),
            Frame("Back patio", "A covered patio for summer evenings by the water."),
        };

        var script = NativePresenterRequestPlanner.SuggestScript(
            new NativePresenterListing("Ocala", "FL", 0, 0, 0, null), frames, input);

        script.Validate(15);
        Assert.Equal("You have to see this sunroom in Ocala, Florida.", script.Opening);
        // The opening already shows the sunroom, so the tour lines start with the next space.
        Assert.DoesNotContain("glass", script.Tour, StringComparison.Ordinal);
        Assert.InRange(script.WordCount, 1, NativePresenterScript.MaximumWordsFor(15));
    }

    private static NativePresenterPlanFrame Frame(string stop, string? highlight) =>
        new(Guid.NewGuid(), "blob", "image/jpeg", stop, highlight, null);
}
