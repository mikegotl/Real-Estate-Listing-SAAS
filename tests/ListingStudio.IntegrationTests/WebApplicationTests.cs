using Microsoft.AspNetCore.Mvc.Testing;

namespace ListingStudio.IntegrationTests;

public sealed class WebApplicationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient client;

    public WebApplicationTests(WebApplicationFactory<Program> factory) => client = factory.CreateClient();

    [Fact]
    public async Task Home_page_is_available()
    {
        using var response = await client.GetAsync("/");

        response.EnsureSuccessStatusCode();
        Assert.Contains("Listing Studio", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
