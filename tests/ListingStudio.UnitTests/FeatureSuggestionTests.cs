using ListingStudio.Domain.Feedback;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class FeatureSuggestionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateTrimsCommentAndLinksRequiredCampaign()
    {
        var campaignId = Guid.NewGuid();

        var suggestion = FeatureSuggestion.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "agent-id",
            "  Add a floor-plan overlay.  ",
            requiredForActiveCampaign: true,
            campaignId,
            Now);

        Assert.Equal("Add a floor-plan overlay.", suggestion.Comment);
        Assert.True(suggestion.RequiredForActiveCampaign);
        Assert.Equal(campaignId, suggestion.CampaignGenerationJobId);
    }

    [Fact]
    public void CreateRejectsMissingCommentAndInconsistentCampaignLink()
    {
        Assert.Throws<ArgumentException>(() => FeatureSuggestion.Create(
            Guid.NewGuid(), Guid.NewGuid(), "agent-id", " ", false, null, Now));
        Assert.Throws<ArgumentException>(() => FeatureSuggestion.Create(
            Guid.NewGuid(), Guid.NewGuid(), "agent-id", "Needed now", true, null, Now));
        Assert.Throws<ArgumentException>(() => FeatureSuggestion.Create(
            Guid.NewGuid(), Guid.NewGuid(), "agent-id", "General idea", false, Guid.NewGuid(), Now));
    }

    [Fact]
    public void GeneralCommentNeedsNoPropertyButACampaignLinkDoes()
    {
        var general = FeatureSuggestion.Create(
            Guid.NewGuid(), null, "agent-id", "The billing page is confusing.", false, null, Now, " /billing ");

        Assert.Null(general.PropertyId);
        Assert.Equal("/billing", general.PagePath);
        Assert.True(general.AdminEmailPending);
        Assert.Throws<ArgumentException>(() => FeatureSuggestion.Create(
            Guid.NewGuid(), null, "agent-id", "Needed now", true, Guid.NewGuid(), Now));
        Assert.Throws<ArgumentException>(() => FeatureSuggestion.Create(
            Guid.NewGuid(), Guid.Empty, "agent-id", "Empty property id", false, null, Now));
    }

    [Fact]
    public void AdminEmailBacksOffAfterFailuresAndStopsAfterTheLastAttempt()
    {
        var suggestion = FeatureSuggestion.Create(
            Guid.NewGuid(), Guid.NewGuid(), "agent-id", "Add a floor-plan overlay.", false, null, Now);

        Assert.True(suggestion.RecordAdminEmailFailure(Now));
        Assert.Equal(Now.AddMinutes(1), suggestion.AdminEmailNextAttemptAtUtc);
        Assert.True(suggestion.RecordAdminEmailFailure(Now));
        Assert.Equal(Now.AddMinutes(2), suggestion.AdminEmailNextAttemptAtUtc);

        for (var attempt = 3; attempt < FeatureSuggestion.MaximumAdminEmailAttempts; attempt++)
        {
            Assert.True(suggestion.RecordAdminEmailFailure(Now));
        }

        Assert.Equal(Now.AddMinutes(60), suggestion.AdminEmailNextAttemptAtUtc);
        Assert.False(suggestion.RecordAdminEmailFailure(Now));
        Assert.False(suggestion.AdminEmailPending);
    }

    [Fact]
    public void DeferringForMissingAdminsDoesNotUseAnAttempt()
    {
        var suggestion = FeatureSuggestion.Create(
            Guid.NewGuid(), null, "agent-id", "Hello", false, null, Now);

        suggestion.DeferAdminEmail(Now.AddHours(1));
        Assert.Equal(0, suggestion.AdminEmailAttemptCount);
        Assert.True(suggestion.AdminEmailPending);

        suggestion.MarkAdminEmailSent(Now.AddHours(1));
        Assert.Equal(Now.AddHours(1), suggestion.AdminEmailSentAtUtc);
        Assert.Null(suggestion.AdminEmailNextAttemptAtUtc);
        Assert.False(suggestion.AdminEmailPending);
    }
}
