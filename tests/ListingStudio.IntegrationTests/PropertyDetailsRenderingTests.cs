using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using ListingStudio.Application.Authentication;
using ListingStudio.Application.Properties;
using ListingStudio.Domain.Properties;
using ListingStudio.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed class PropertyDetailsRenderingTests(PostgreSqlWebApplicationFixture fixture)
    : IClassFixture<PostgreSqlWebApplicationFixture>
{
    [Fact]
    public async Task DetailsPageRendersIndependentDataPanelsWithoutDbContextConcurrency()
    {
        await using var factory = fixture.CreateFactory(services =>
        {
            services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = HeaderAuthenticationHandler.SchemeName;
                    options.DefaultChallengeScheme = HeaderAuthenticationHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(
                    HeaderAuthenticationHandler.SchemeName,
                    _ => { });
        });
        await using var scope = factory.Services.CreateAsyncScope();
        var registration = scope.ServiceProvider.GetRequiredService<IAccountRegistrationService>();
        var properties = scope.ServiceProvider.GetRequiredService<IPropertyService>();
        var registered = await registration.RegisterAsync(new RegisterAccountCommand(
            $"details-owner-{Guid.NewGuid():N}@example.com",
            "Password123",
            $"Details Realty {Guid.NewGuid():N}"));
        Assert.True(registered.Succeeded, string.Join(", ", registered.Errors));
        var propertyId = await properties.CreateAsync(registered.UserId!, new PropertyInput(
            "121 E 7th St",
            null,
            "Chuluota",
            "FL",
            "32766",
            420_000m,
            3,
            2m,
            1_850,
            0.2m,
            2001,
            PropertyType.SingleFamily,
            "Regression property.",
            ListingStatus.Draft));

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            HeaderAuthenticationHandler.SchemeName,
            registered.UserId);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("de-DE");

        using var response = await client.GetAsync($"/properties/{propertyId}");

        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("121 E 7th St", content, StringComparison.Ordinal);
        Assert.Contains("Listing photos", content, StringComparison.Ordinal);
        Assert.Contains("Marketing story", content, StringComparison.Ordinal);
        Assert.Contains("Campaign", content, StringComparison.Ordinal);
        Assert.Contains("$420,000", content, StringComparison.Ordinal);
        Assert.Contains("1,850", content, StringComparison.Ordinal);
        Assert.Contains("mobile-nav", content, StringComparison.Ordinal);
        Assert.Contains("Next step", content, StringComparison.Ordinal);
        Assert.Contains("Add listing photos", content, StringComparison.Ordinal);
        Assert.Contains("Story &amp; script", content, StringComparison.Ordinal);
        Assert.Contains("Neighborhood", content, StringComparison.Ordinal);
        Assert.Contains("External listing research", content, StringComparison.Ordinal);
        Assert.Contains("Zillow home link", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkspaceAndPropertyListRenderPropertyCards()
    {
        await using var factory = fixture.CreateFactory(services =>
        {
            services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = HeaderAuthenticationHandler.SchemeName;
                    options.DefaultChallengeScheme = HeaderAuthenticationHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(
                    HeaderAuthenticationHandler.SchemeName,
                    _ => { });
        });
        await using var scope = factory.Services.CreateAsyncScope();
        var registration = scope.ServiceProvider.GetRequiredService<IAccountRegistrationService>();
        var properties = scope.ServiceProvider.GetRequiredService<IPropertyService>();
        var registered = await registration.RegisterAsync(new RegisterAccountCommand(
            $"cards-owner-{Guid.NewGuid():N}@example.com",
            "Password123",
            $"Cards Realty {Guid.NewGuid():N}"));
        Assert.True(registered.Succeeded, string.Join(", ", registered.Errors));
        var propertyId = await properties.CreateAsync(registered.UserId!, new PropertyInput(
            "48 Harbor View Rd",
            null,
            "Annapolis",
            "MD",
            "21403",
            1_250_000m,
            4,
            3.5m,
            3_200,
            0.4m,
            1998,
            PropertyType.SingleFamily,
            "Waterfront regression property.",
            ListingStatus.Active));

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            HeaderAuthenticationHandler.SchemeName,
            registered.UserId);

        using var workspace = await client.GetAsync("/auth");
        workspace.EnsureSuccessStatusCode();
        var workspaceContent = await workspace.Content.ReadAsStringAsync();
        Assert.Contains("Needs your attention", workspaceContent, StringComparison.Ordinal);
        Assert.Contains("48 Harbor View Rd", workspaceContent, StringComparison.Ordinal);
        Assert.Contains("Add listing photos", workspaceContent, StringComparison.Ordinal);
        Assert.Contains($"/properties/{propertyId}?tab=media", workspaceContent, StringComparison.Ordinal);
        Assert.Contains("PLAN AND USAGE", workspaceContent, StringComparison.Ordinal);

        using var list = await client.GetAsync("/properties");
        list.EnsureSuccessStatusCode();
        var listContent = await list.Content.ReadAsStringAsync();
        Assert.Contains("property-card", listContent, StringComparison.Ordinal);
        Assert.Contains("48 Harbor View Rd", listContent, StringComparison.Ordinal);
        Assert.Contains("$1,250,000", listContent, StringComparison.Ordinal);
        Assert.Contains("3,200 sq ft", listContent, StringComparison.Ordinal);
        Assert.Contains("No photos yet", listContent, StringComparison.Ordinal);

        using var mediaTab = await client.GetAsync($"/properties/{propertyId}?tab=media");
        mediaTab.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task MediaTabDrawsTheWalkthroughMapFromStoryboardOrderAndDirections()
    {
        await using var factory = fixture.CreateFactory(services =>
        {
            services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = HeaderAuthenticationHandler.SchemeName;
                    options.DefaultChallengeScheme = HeaderAuthenticationHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(
                    HeaderAuthenticationHandler.SchemeName,
                    _ => { });
        });
        await using var scope = factory.Services.CreateAsyncScope();
        var registration = scope.ServiceProvider.GetRequiredService<IAccountRegistrationService>();
        var properties = scope.ServiceProvider.GetRequiredService<IPropertyService>();
        var mediaService = scope.ServiceProvider.GetRequiredService<IPropertyMediaService>();
        var registered = await registration.RegisterAsync(new RegisterAccountCommand(
            $"map-owner-{Guid.NewGuid():N}@example.com",
            "Password123",
            $"Map Realty {Guid.NewGuid():N}"));
        Assert.True(registered.Succeeded, string.Join(", ", registered.Errors));
        var userId = registered.UserId!;
        var propertyId = await properties.CreateAsync(userId, new PropertyInput(
            "9 Lantern Way",
            null,
            "Annapolis",
            "MD",
            "21403",
            780_000m,
            3,
            2m,
            2_000,
            0.2m,
            2005,
            PropertyType.SingleFamily,
            "Walkthrough map regression property.",
            ListingStatus.Draft));

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            HeaderAuthenticationHandler.SchemeName,
            userId);

        await UploadAsync(mediaService, userId, propertyId, "front.png", WalkthroughStoryboard.ExteriorFront);
        var oneStop = await (await client.GetAsync($"/properties/{propertyId}?tab=media")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("walkthrough-map-heading", oneStop, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Media sections\"", oneStop, StringComparison.Ordinal);
        Assert.DoesNotContain("getElementById('media-map')", oneStop, StringComparison.Ordinal);

        await UploadAsync(mediaService, userId, propertyId, "foyer.png", WalkthroughStoryboard.EntryFoyer);
        await UploadAsync(mediaService, userId, propertyId, "kitchen.png", "Kitchen");
        Assert.True(await mediaService.SetStopDirectionAsync(userId, propertyId, "Kitchen", WalkthroughDirection.Left));

        using var response = await client.GetAsync($"/properties/{propertyId}?tab=media");
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("walkthrough-map-heading", content, StringComparison.Ordinal);
        Assert.Contains("Show Kitchen on the walkthrough map", content, StringComparison.Ordinal);
        Assert.Contains("Show Exterior front on the walkthrough map", content, StringComparison.Ordinal);
        Assert.Contains("Main level", content, StringComparison.Ordinal);
        Assert.Contains("Stop 1, Exterior front, where the walkthrough starts", content, StringComparison.Ordinal);
        Assert.Contains(
            "Stop 2, Entry / foyer, straight ahead from the previous stop (assumed)",
            content,
            StringComparison.Ordinal);
        Assert.Contains("Stop 3, Kitchen, turn left from the previous stop", content, StringComparison.Ordinal);
        Assert.Contains("getElementById('media-map')", content, StringComparison.Ordinal);
        Assert.Contains("<span class=\"media-nav-count\">3</span>", content, StringComparison.Ordinal);
        Assert.Contains("Extra photos", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtraPhotosRenderAccessibleBulkSelectionAndActions()
    {
        await using var factory = fixture.CreateFactory(services =>
        {
            services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = HeaderAuthenticationHandler.SchemeName;
                    options.DefaultChallengeScheme = HeaderAuthenticationHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(
                    HeaderAuthenticationHandler.SchemeName,
                    _ => { });
        });
        await using var scope = factory.Services.CreateAsyncScope();
        var registration = scope.ServiceProvider.GetRequiredService<IAccountRegistrationService>();
        var properties = scope.ServiceProvider.GetRequiredService<IPropertyService>();
        var mediaService = scope.ServiceProvider.GetRequiredService<IPropertyMediaService>();
        var registered = await registration.RegisterAsync(new RegisterAccountCommand(
            $"bulk-extra-owner-{Guid.NewGuid():N}@example.com",
            "Password123",
            $"Bulk Extra Realty {Guid.NewGuid():N}"));
        Assert.True(registered.Succeeded, string.Join(", ", registered.Errors));
        var userId = registered.UserId!;
        var propertyId = await properties.CreateAsync(userId, new PropertyInput(
            "72 Gallery Lane",
            null,
            "Savannah",
            "GA",
            "31401",
            525_000m,
            3,
            2m,
            1_750,
            0.15m,
            2010,
            PropertyType.SingleFamily,
            "Bulk gallery regression property.",
            ListingStatus.Draft));
        await UploadAsync(mediaService, userId, propertyId, "spare-one.png", null, toGallery: true);
        await UploadAsync(mediaService, userId, propertyId, "spare-two.png", null, toGallery: true);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            HeaderAuthenticationHandler.SchemeName,
            userId);
        using var response = await client.GetAsync($"/properties/{propertyId}?tab=media");
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();

        Assert.Contains("aria-label=\"Extra photo bulk actions\"", content, StringComparison.Ordinal);
        Assert.Contains("Select all", content, StringComparison.Ordinal);
        Assert.Contains("Move selected", content, StringComparison.Ordinal);
        Assert.Contains("Fix selected", content, StringComparison.Ordinal);
        Assert.Contains("Delete selected", content, StringComparison.Ordinal);
        Assert.Contains("Start Again", content, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Start media over\"", content, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Select spare-one.png\"", content, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Select spare-two.png\"", content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FindRoomPhotosIsOfferedOnlyWhenRoomRecognitionIsEnabled(bool enabled)
    {
        await using var factory = fixture.CreateFactory(services =>
        {
            services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = HeaderAuthenticationHandler.SchemeName;
                    options.DefaultChallengeScheme = HeaderAuthenticationHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(
                    HeaderAuthenticationHandler.SchemeName,
                    _ => { });
            services.AddSingleton(Options.Create(new MediaAnalysisOptions { Enabled = enabled }));
        });
        await using var scope = factory.Services.CreateAsyncScope();
        var registration = scope.ServiceProvider.GetRequiredService<IAccountRegistrationService>();
        var properties = scope.ServiceProvider.GetRequiredService<IPropertyService>();
        var videos = scope.ServiceProvider.GetRequiredService<IPropertyVideoService>();
        var registered = await registration.RegisterAsync(new RegisterAccountCommand(
            $"room-photos-owner-{Guid.NewGuid():N}@example.com",
            "Password123",
            $"Room Photo Realty {Guid.NewGuid():N}"));
        Assert.True(registered.Succeeded, string.Join(", ", registered.Errors));
        var userId = registered.UserId!;
        var propertyId = await properties.CreateAsync(userId, new PropertyInput(
            "14 Quay Street",
            null,
            "Portland",
            "ME",
            "04101",
            640_000m,
            3,
            2m,
            1_900,
            0.1m,
            1999,
            PropertyType.SingleFamily,
            "Room photo regression property.",
            ListingStatus.Draft));
        await using (var upload = new MemoryStream([0, 0, 0, 20, 102, 116, 121, 112, 105, 115, 111, 109, 0, 0, 0, 0]))
        {
            await videos.UploadAsync(userId, propertyId, new PropertyVideoUpload("Walkthrough.mp4", "video/mp4", upload.Length, upload));
        }

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            HeaderAuthenticationHandler.SchemeName,
            userId);
        using var response = await client.GetAsync($"/properties/{propertyId}?tab=media");
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();

        Assert.Contains("Walkthrough.mp4", content, StringComparison.Ordinal);
        Assert.Equal(enabled, content.Contains("Find room photos", StringComparison.Ordinal));
    }

    private static async Task UploadAsync(
        IPropertyMediaService mediaService,
        string userId,
        Guid propertyId,
        string filename,
        string? walkthroughStop,
        bool toGallery = false)
    {
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Z+VAAAAAASUVORK5CYII=");
        await using var content = new MemoryStream(png);
        await mediaService.UploadAsync(
            userId,
            propertyId,
            new PropertyMediaUpload(filename, "image/png", content.Length, content, walkthroughStop, toGallery));
    }
}

public sealed class HeaderAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "IntegrationHeader";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!AuthenticationHeaderValue.TryParse(Request.Headers.Authorization, out var authorization)
            || !string.Equals(authorization.Scheme, SchemeName, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(authorization.Parameter))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, authorization.Parameter)],
            SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
