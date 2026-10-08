using ListingStudio.Domain.Properties;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class PropertyNarrationScriptTests
{
    [Fact]
    public void ReplacementRevokesPriorMarketingAcceptance()
    {
        var acceptedAt = DateTimeOffset.UtcNow;
        var script = PropertyNarrationScript.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "scripts/first.txt",
            "first.txt",
            "text/plain",
            100,
            "Welcome to this thoughtfully designed home.",
            acceptedAt.AddMinutes(-5));
        script.SetMarketingUseAccepted(true, "owner-1", acceptedAt);

        script.Replace(
            "scripts/replacement.txt",
            "replacement.txt",
            "text/plain",
            120,
            "This replacement requires a fresh marketing review.",
            acceptedAt.AddMinutes(5));

        Assert.False(script.MarketingUseAccepted);
        Assert.Null(script.MarketingUseAcceptedAtUtc);
        Assert.Null(script.MarketingUseAcceptedByUserId);
        Assert.Equal("This replacement requires a fresh marketing review.", script.ExtractedText);
    }

    [Fact]
    public void AcceptanceRecordsTheResponsibleUserAndCanBeRevoked()
    {
        var now = DateTimeOffset.UtcNow;
        var script = PropertyNarrationScript.Create(
            Guid.NewGuid(), Guid.NewGuid(), "scripts/script.md", "script.md", "text/markdown", 50,
            "A concise approved marketing narration.", now);

        script.SetMarketingUseAccepted(true, "owner-2", now.AddMinutes(1));

        Assert.True(script.MarketingUseAccepted);
        Assert.Equal("owner-2", script.MarketingUseAcceptedByUserId);
        Assert.Equal(now.AddMinutes(1), script.MarketingUseAcceptedAtUtc);

        script.SetMarketingUseAccepted(false, "owner-2", now.AddMinutes(2));
        Assert.False(script.MarketingUseAccepted);
        Assert.Null(script.MarketingUseAcceptedAtUtc);
        Assert.Null(script.MarketingUseAcceptedByUserId);
    }
}
