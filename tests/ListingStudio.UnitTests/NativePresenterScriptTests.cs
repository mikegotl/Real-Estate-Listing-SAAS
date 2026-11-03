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
        var error = Assert.Throws<ArgumentException>(tooLong.Validate);
        Assert.Contains("75", error.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(new NativePresenterScript("Hi.", " ", "Bye.").Validate);
        Assert.Throws<ArgumentException>(new NativePresenterScript("Say \"hi\".", "Tour.", "Bye.").Validate);
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
        Assert.Equal("Welcome to your next home in Ocala, FL. Let me show you around.", script.Opening);
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

    private static NativePresenterPlanFrame Frame(string stop, string? highlight) =>
        new(Guid.NewGuid(), "blob", "image/jpeg", stop, highlight, null);
}
