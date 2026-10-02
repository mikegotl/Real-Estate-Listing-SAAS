using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed class WebApplicationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient client;

    public WebApplicationTests(WebApplicationFactory<Program> factory) => client = factory.CreateClient();

    [Fact]
    public async Task HomePageIsAvailable()
    {
        using var response = await client.GetAsync("/");

        response.EnsureSuccessStatusCode();
        Assert.Contains("Listing Studio", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
