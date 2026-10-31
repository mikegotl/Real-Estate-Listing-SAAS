using ListingStudio.Infrastructure.Administration;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class TesterWelcomeEmailTemplateTests
{
    [Fact]
    public void BrandedWelcomeIncludesAClickableSiteButtonAndPlainContent()
    {
        const string loginUrl = "https://creativelistingstudio.com/Account/Login";

        var html = TesterWelcomeEmailTemplate.CreateHtml(
            "Hi,\n\nUsername: tester@example.com\nTemporary password: A&B<123>",
            loginUrl);

        Assert.Contains("Creative Listing Studio", html, StringComparison.Ordinal);
        Assert.Contains("Open CreativeListingStudio.com", html, StringComparison.Ordinal);
        Assert.Contains($"href=\"{loginUrl}\"", html, StringComparison.Ordinal);
        Assert.Contains("Username: tester@example.com", html, StringComparison.Ordinal);
        Assert.Contains("Temporary password: A&amp;B&lt;123&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("A&B<123>", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "https://creativelistingstudio.com/Account/Login")]
    [InlineData("", "https://creativelistingstudio.com/Account/Login")]
    [InlineData("https://preview.example.com/path", "https://preview.example.com/Account/Login")]
    public void LoginLinkUsesTheConfiguredSiteOriginOrProductionFallback(string? configured, string expected)
    {
        Assert.Equal(expected, TesterWelcomeEmailTemplate.LoginUrl(configured));
    }
}
