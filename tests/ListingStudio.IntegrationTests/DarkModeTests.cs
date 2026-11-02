using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed class DarkModeTests(PostgreSqlWebApplicationFixture fixture)
    : IClassFixture<PostgreSqlWebApplicationFixture>
{
    [Fact]
    public async Task PagesFollowTheDeviceUntilTheVisitorChoosesATheme()
    {
        using var client = fixture.Factory.CreateClient();
        var html = await client.GetStringAsync("/");

        Assert.Contains("<html lang=\"en\">", html, StringComparison.Ordinal);
        Assert.Contains("data-theme-toggle", html, StringComparison.Ordinal);
        // The dark palette is always sent: once for the saved choice and once behind prefers-color-scheme.
        Assert.Contains(":root[data-theme=\"dark\"]{--mud-palette-black:", html, StringComparison.Ordinal);
        Assert.Contains("@media (prefers-color-scheme: dark){:root:not([data-theme=\"light\"]){", html, StringComparison.Ordinal);
        Assert.Contains("--mud-palette-background: rgba(13,24,36,1);", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dark")]
    [InlineData("light")]
    public async Task SavedChoiceIsRenderedOnTheFirstPaint(string theme)
    {
        using var client = fixture.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("Cookie", $"ls-theme={theme}");

        using var response = await client.SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains($"<html lang=\"en\" data-theme=\"{theme}\">", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownCookieValueFallsBackToTheDeviceSetting()
    {
        using var client = fixture.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("Cookie", "ls-theme=%22%3E%3Cscript%3E");

        using var response = await client.SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("<html lang=\"en\">", html, StringComparison.Ordinal);
    }
}
