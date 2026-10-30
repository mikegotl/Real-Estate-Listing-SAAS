using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed class ArticlesPageTests(PostgreSqlWebApplicationFixture fixture)
    : IClassFixture<PostgreSqlWebApplicationFixture>
{
    private const string StarterArticle = "/articles/creative-social-media-campaigns-for-real-estate-agents";

    [Fact]
    public async Task ArticlesPageIsPublicAndListsBothShelves()
    {
        using var client = AnonymousClient();

        using var response = await client.GetAsync("/articles");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("<title>Articles for real estate agents | Creative Listing Studio</title>", html);
        Assert.Contains("<meta name=\"description\"", html);
        Assert.Contains("rel=\"canonical\" href=\"https://creativelistingstudio.com/articles\"", html);
        Assert.Contains("Recently released", html);
        Assert.Contains("Archived", html);
        Assert.Contains($"href=\"{StarterArticle}\"", html);
    }

    [Fact]
    public async Task ArticleIsPublicWithSearchMetadataAndLivePlanPrices()
    {
        using var client = AnonymousClient();

        using var response = await client.GetAsync(StarterArticle);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("| Creative Listing Studio</title>", html);
        Assert.Contains("<meta property=\"og:type\" content=\"article\"", html);
        Assert.Contains($"rel=\"canonical\" href=\"https://creativelistingstudio.com{StarterArticle}\"", html);
        Assert.Contains("application/ld+json", html);
        Assert.Contains("\"@type\":\"Article\"", html);
        Assert.Contains("datePublished", html);
        // The comparison reads plan prices from billing settings ($99 / $299 by default) and adds up the separate tools.
        Assert.Contains("$91.99", html);
        Assert.Contains("$99", html);
        Assert.Contains("$299", html);
        Assert.Contains("https://cms.nar.realtor/", html);
    }

    [Fact]
    public async Task UnknownArticleReturnsNotFound()
    {
        using var client = AnonymousClient();

        using var response = await client.GetAsync("/articles/no-such-article");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private HttpClient AnonymousClient() => fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
    });
}
