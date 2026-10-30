using System.Net;
using ListingStudio.Application.Authentication;
using ListingStudio.Application.Leads;
using ListingStudio.Application.Properties;
using ListingStudio.Domain.Properties;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed class SearchVisibilityTests(PostgreSqlWebApplicationFixture fixture)
    : IClassFixture<PostgreSqlWebApplicationFixture>
{
    private const string PublicBase = "https://creativelistingstudio.com";

    [Fact]
    public async Task RobotsPointsAtSitemapOnPublicDomainAndKeepsAppPagesOut()
    {
        using var client = fixture.Factory.CreateClient();
        var robots = await client.GetStringAsync("/robots.txt");

        Assert.Contains("User-agent: *", robots, StringComparison.Ordinal);
        Assert.Contains("Disallow: /properties", robots, StringComparison.Ordinal);
        Assert.Contains("Disallow: /Account/Login", robots, StringComparison.Ordinal);
        Assert.DoesNotContain("Disallow: /listing", robots, StringComparison.Ordinal);
        Assert.Contains($"Sitemap: {PublicBase}/sitemap.xml", robots, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SitemapListsMarketingPagesAndOnlyPublishedListings()
    {
        var published = await PublishListingAsync("seo-published", publish: true);
        var hidden = await PublishListingAsync("seo-hidden", publish: false);
        using var client = fixture.Factory.CreateClient();

        using var response = await client.GetAsync("/sitemap.xml");
        var sitemap = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/xml", response.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", sitemap, StringComparison.Ordinal);
        Assert.Contains($"<loc>{PublicBase}/</loc>", sitemap, StringComparison.Ordinal);
        Assert.Contains($"<loc>{PublicBase}/privacy</loc>", sitemap, StringComparison.Ordinal);
        Assert.Contains($"<loc>{PublicBase}/articles</loc>", sitemap, StringComparison.Ordinal);
        Assert.Contains($"<loc>{PublicBase}/articles/", sitemap, StringComparison.Ordinal);
        Assert.Contains($"<loc>{PublicBase}/listing/{published}</loc>", sitemap, StringComparison.Ordinal);
        Assert.DoesNotContain(hidden, sitemap, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HomePageHasDescriptionCanonicalPreviewAndStructuredData()
    {
        using var client = fixture.Factory.CreateClient();
        var html = await client.GetStringAsync("/");

        Assert.Contains("<title>AI Real Estate Listing Videos for TikTok, Reels and YouTube | Creative Listing Studio</title>", html, StringComparison.Ordinal);
        Assert.Contains("<meta name=\"description\" content=\"Turn listing photos", html, StringComparison.Ordinal);
        Assert.Contains($"<link rel=\"canonical\" href=\"{PublicBase}/\"", html, StringComparison.Ordinal);
        Assert.Contains($"<meta property=\"og:image\" content=\"{PublicBase}/images/og-default.jpg\"", html, StringComparison.Ordinal);
        Assert.Contains("<script type=\"application/ld+json\">", html, StringComparison.Ordinal);
        Assert.Contains("\"@type\":\"SoftwareApplication\"", html, StringComparison.Ordinal);
        Assert.Contains("rel=\"icon\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/privacy\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignInPagesAreKeptOutOfSearchAndRegisterIsIndexable()
    {
        using var client = fixture.Factory.CreateClient();
        var login = await client.GetStringAsync("/Account/Login");
        var register = await client.GetStringAsync("/Account/Register");

        Assert.Contains("<meta name=\"robots\" content=\"noindex\"", login, StringComparison.Ordinal);
        Assert.DoesNotContain("<meta name=\"robots\" content=\"noindex\"", register, StringComparison.Ordinal);
        Assert.Contains("<title>Create your free account | Creative Listing Studio</title>", register, StringComparison.Ordinal);
        var pageHeading = register.IndexOf("<h1", StringComparison.Ordinal);
        var firstSection = register.IndexOf("<h2", StringComparison.Ordinal);
        Assert.InRange(pageHeading, 0, firstSection < 0 ? int.MaxValue : firstSection);
    }

    [Fact]
    public async Task PublishedListingHasSearchTitlePreviewAndListingData()
    {
        var slug = await PublishListingAsync("seo-listing", publish: true);
        using var client = fixture.Factory.CreateClient();

        var html = WebUtility.HtmlDecode(await client.GetStringAsync($"/listing/{slug}?utm_source=facebook"));

        Assert.Contains("<title>123 Main Street, Raleigh, NC · 3 bd · 2.5 ba · $450,000 | Creative Listing Studio</title>", html, StringComparison.Ordinal);
        Assert.Contains("<meta name=\"description\" content=\"A comfortable home.\"", html, StringComparison.Ordinal);
        Assert.Contains($"<link rel=\"canonical\" href=\"{PublicBase}/listing/{slug}\"", html, StringComparison.Ordinal);
        Assert.Contains("\"@type\":\"RealEstateListing\"", html, StringComparison.Ordinal);
        Assert.Contains("\"price\":\"450000\"", html, StringComparison.Ordinal);
        Assert.Contains("Creative Listing Studio</a>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnpublishedOrUnknownListingReturnsNotFound()
    {
        var hidden = await PublishListingAsync("seo-unpublished", publish: false);
        using var client = fixture.Factory.CreateClient();

        using var unpublished = await client.GetAsync($"/listing/{hidden}");
        using var unknown = await client.GetAsync($"/listing/{Guid.NewGuid():N}");

        Assert.Equal(HttpStatusCode.NotFound, unpublished.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Contains("Page not found", await unpublished.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HeadRequestsForPagesSucceedWithoutBody()
    {
        using var client = fixture.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Head, "/privacy");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task AzureAndWwwAddressesRedirectPagesToPublicDomain()
    {
        using var client = fixture.Factory.CreateClient(new() { AllowAutoRedirect = false });

        using var azure = await SendAsync(client, HttpMethod.Get, "liststudio-web.example.centralus.azurecontainerapps.io", "/privacy?ref=1");
        using var www = await SendAsync(client, HttpMethod.Get, "www.creativelistingstudio.com", "/");
        using var health = await SendAsync(client, HttpMethod.Get, "liststudio-web.example.centralus.azurecontainerapps.io", "/health/live");
        using var post = await SendAsync(client, HttpMethod.Post, "liststudio-web.example.centralus.azurecontainerapps.io", "/billing/stripe-webhook");
        using var local = await SendAsync(client, HttpMethod.Get, "localhost", "/privacy");

        Assert.Equal(HttpStatusCode.MovedPermanently, azure.StatusCode);
        Assert.Equal($"{PublicBase}/privacy?ref=1", azure.Headers.Location?.AbsoluteUri);
        Assert.Equal(HttpStatusCode.MovedPermanently, www.StatusCode);
        Assert.Equal($"{PublicBase}/", www.Headers.Location?.AbsoluteUri);
        Assert.NotEqual(HttpStatusCode.MovedPermanently, health.StatusCode);
        Assert.NotEqual(HttpStatusCode.MovedPermanently, post.StatusCode);
        Assert.Equal(HttpStatusCode.OK, local.StatusCode);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string host, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Host = host;
        return client.SendAsync(request);
    }

    private async Task<string> PublishListingAsync(string prefix, bool publish)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var registration = scope.ServiceProvider.GetRequiredService<IAccountRegistrationService>();
        var owner = await registration.RegisterAsync(new RegisterAccountCommand(
            $"{prefix}-{Guid.NewGuid():N}@example.com",
            "Password123",
            $"{prefix} Realty {Guid.NewGuid():N}"));
        Assert.True(owner.Succeeded, string.Join(", ", owner.Errors));
        var propertyId = await scope.ServiceProvider.GetRequiredService<IPropertyService>().CreateAsync(
            owner.UserId!,
            new PropertyInput(
                "123 Main Street", null, "Raleigh", "NC", "27601", 450_000m,
                3, 2.5m, 2_100, 0.25m, 1998, PropertyType.SingleFamily,
                "A comfortable home.", ListingStatus.Active));
        var settings = await scope.ServiceProvider.GetRequiredService<ILeadGenerationService>()
            .SaveCaptureSettingsAsync(owner.UserId!, propertyId, null, publish);
        Assert.NotNull(settings);
        return settings.PublicSlug;
    }
}
