using System.Net;
using System.Security.Claims;
using ListingStudio.Application.Administration;
using ListingStudio.Application.Authentication;
using ListingStudio.Domain.Administration;
using ListingStudio.Infrastructure.Identity;
using ListingStudio.Infrastructure.Persistence;
using ListingStudio.Web.Components.Articles;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ListingStudio.IntegrationTests;

/// <summary>Prompt 11: the "One Listing Four TikToks" article, the product headline and the tracked guide links, all behind VerticalVideoLibraryV1.</summary>
public sealed class VerticalLibraryArticleTests(PostgreSqlWebApplicationFixture fixture)
    : IClassFixture<PostgreSqlWebApplicationFixture>
{
    private const string ArticlePath = "/articles/one-listing-four-tiktoks";
    private const string OldHeadline = "Every listing deserves a";
    private const string NewHeadline = "Turn one listing into the vertical videos";

    [Fact]
    public async Task TheArticleHeadlineAndLinksFollowTheSwitch()
    {
        await using var factory = fixture.CreateFactory(_ => { });
        var (admin, adminOrganization) = await RegisterAsync(factory, "article-admin", platformAdmin: true);
        var (member, memberOrganization) = await RegisterAsync(factory, "article-member", platformAdmin: false);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Off (no saved switch): the feature article doesn't exist, and nothing about it is emitted anywhere.
        await AssertHiddenAsync(client);
        using (var go = await client.GetAsync($"/go/article/one-listing-four-tiktoks?from={ArticleLinks.Placements.HelpChat}"))
        {
            Assert.Equal(HttpStatusCode.Redirect, go.StatusCode);
            Assert.Equal("/articles", go.Headers.Location!.OriginalString);
        }

        Assert.Equal(0, await ClicksAsync(factory));

        // Pilot only: still hidden from the public and the sitemap; a pilot member can see it in the app.
        await ConfigureAsync(factory, admin, global: true, everyone: false, memberOrganization);
        await AssertHiddenAsync(client);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var visibility = scope.ServiceProvider.GetRequiredService<ArticleVisibility>();
            Assert.True(await visibility.CanSeeAsync(ArticleLibrary.VerticalLibraryGuide, User(member)));
            Assert.False(await visibility.CanSeeAsync(ArticleLibrary.VerticalLibraryGuide, User(admin)));
            Assert.False(await visibility.CanSeeAsync(ArticleLibrary.VerticalLibraryGuide, new ClaimsPrincipal(new ClaimsIdentity())));
            Assert.True(await visibility.IsVerticalLibraryOnAsync(User(member)));
            Assert.DoesNotContain(await visibility.PublicAsync(), entry => entry.Summary.FeatureKey is not null);
        }

        // On for everyone: the article, its metadata, the sitemap entry, the new headline and the tracked links.
        await ConfigureAsync(factory, admin, global: true, everyone: true);
        using (var response = await client.GetAsync(ArticlePath))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("<title>One Listing Four TikToks | Creative Listing Studio</title>", html, StringComparison.Ordinal);
            Assert.Contains("content=\"Learn how Listing Studio analyzes verified property facts and media to recommend purpose-built vertical videos for reach engagement and buyer interest.\"", html, StringComparison.Ordinal);
            Assert.Contains($"rel=\"canonical\" href=\"https://creativelistingstudio.com{ArticlePath}\"", html, StringComparison.Ordinal);
            Assert.Contains("<meta property=\"og:title\" content=\"One Listing Four TikToks\"", html, StringComparison.Ordinal);
            Assert.Contains("\"@type\":\"Article\"", html, StringComparison.Ordinal);
            Assert.Contains("\"headline\":\"One Listing Four TikToks\"", html, StringComparison.Ordinal);
            Assert.Contains("\"datePublished\":\"2026-11-01\"", html, StringComparison.Ordinal);
            Assert.Contains("directional research, not a forecast or a guarantee", html, StringComparison.Ordinal);
            foreach (var section in new[] { "one-walkthrough", "how-it-chooses", "four-formats", "inputs-fallbacks", "verification", "review", "metrics" })
            {
                Assert.Contains($"id=\"{section}\"", html, StringComparison.Ordinal);
            }

            foreach (var banned in new[] { "guaranteed viral", "guaranteed views", "best performing", "best-performing", "guaranteed results" })
            {
                Assert.DoesNotContain(banned, html, StringComparison.OrdinalIgnoreCase);
            }

            // One h1, and every table has headers.
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "<h1[ >]"));
            Assert.Contains("<th scope=\"col\">Format</th>", html, StringComparison.Ordinal);
        }

        Assert.Contains($"href=\"{ArticlePath}\"", await client.GetStringAsync("/articles"), StringComparison.Ordinal);
        Assert.Contains($"https://creativelistingstudio.com{ArticlePath}", await client.GetStringAsync("/sitemap.xml"), StringComparison.Ordinal);
        var home = await client.GetStringAsync("/");
        Assert.Contains(NewHeadline, home, StringComparison.Ordinal);
        Assert.DoesNotContain(OldHeadline, home, StringComparison.Ordinal);
        Assert.Contains("/go/article/one-listing-four-tiktoks?from=article-cta", home, StringComparison.Ordinal);
        Assert.Contains("data-testid=\"vertical-library-bullet\"",
            await client.GetStringAsync("/articles/creative-social-media-campaigns-for-real-estate-agents"), StringComparison.Ordinal);

        using (var go = await client.GetAsync($"/go/article/one-listing-four-tiktoks?from={ArticleLinks.Placements.HelpChat}"))
        {
            Assert.Equal(ArticlePath, go.Headers.Location!.OriginalString);
        }

        using (var go = await client.GetAsync("/go/article/one-listing-four-tiktoks?from=article-cta&to=%2FAccount%2FRegister"))
        {
            Assert.Equal("/Account/Register", go.Headers.Location!.OriginalString);
        }

        using (var go = await client.GetAsync("/go/article/one-listing-four-tiktoks?from=somewhere&to=https%3A%2F%2Fexample.com%2F"))
        {
            Assert.Equal(ArticlePath, go.Headers.Location!.OriginalString);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var clicks = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().OperationalEvents.AsNoTracking()
                .Where(item => item.Kind == ArticleEndpoints.ClickKind).ToListAsync();
            Assert.Equal(3, clicks.Count);
            Assert.All(clicks, click => Assert.Equal("one-listing-four-tiktoks", click.Category));
            Assert.Equal(["article-cta", "help-chat", "other"], clicks.Select(click => click.Outcome).Order(StringComparer.Ordinal));
        }

        // Global off beats everyone-on: back to the old copy, and the article is gone again.
        await ConfigureAsync(factory, admin, global: false, everyone: true);
        await AssertHiddenAsync(client);
        _ = adminOrganization;
    }

    private static async Task AssertHiddenAsync(HttpClient client)
    {
        using (var response = await client.GetAsync(ArticlePath))
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("One Listing Four TikToks", html, StringComparison.Ordinal);
            Assert.DoesNotContain("article:published_time", html, StringComparison.Ordinal);
            Assert.DoesNotContain("\"@type\":\"Article\"", html, StringComparison.Ordinal);
            Assert.DoesNotContain("one-listing-four-tiktoks", html, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("one-listing-four-tiktoks", await client.GetStringAsync("/articles"), StringComparison.Ordinal);
        Assert.DoesNotContain("one-listing-four-tiktoks", await client.GetStringAsync("/sitemap.xml"), StringComparison.Ordinal);
        var home = await client.GetStringAsync("/");
        Assert.Contains(OldHeadline, home, StringComparison.Ordinal);
        Assert.DoesNotContain(NewHeadline, home, StringComparison.Ordinal);
        Assert.DoesNotContain("one-listing-four-tiktoks",
            await client.GetStringAsync("/articles/creative-social-media-campaigns-for-real-estate-agents"), StringComparison.Ordinal);
    }

    private static async Task ConfigureAsync(ListingStudioWebApplicationFactory factory, string admin, bool global, bool everyone, params Guid[] pilots)
    {
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IFeatureStateService>()
                .ConfigureAsync(admin, FeatureFlagKeys.VerticalVideoLibraryV1, global, everyone, pilots);
        }

        // Past the 15 s feature-state cache.
        factory.TimeProvider.Advance(TimeSpan.FromSeconds(20));
    }

    private static async Task<int> ClicksAsync(ListingStudioWebApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().OperationalEvents
            .CountAsync(item => item.Kind == ArticleEndpoints.ClickKind);
    }

    private static ClaimsPrincipal User(string userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test"));

    private static async Task<(string UserId, Guid OrganizationId)> RegisterAsync(ListingStudioWebApplicationFactory factory, string prefix, bool platformAdmin)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<IAccountRegistrationService>().RegisterAsync(new RegisterAccountCommand(
            $"{prefix}-{Guid.NewGuid():N}@example.com", "Password123", $"{prefix} Realty {Guid.NewGuid():N}"));
        Assert.True(result.Succeeded, string.Join(", ", result.Errors));
        if (platformAdmin)
        {
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            if (!await roles.RoleExistsAsync(PlatformAdministration.Role))
            {
                Assert.True((await roles.CreateAsync(new IdentityRole(PlatformAdministration.Role))).Succeeded);
            }

            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            Assert.True((await users.AddToRoleAsync((await users.FindByIdAsync(result.UserId!))!, PlatformAdministration.Role)).Succeeded);
        }

        return (result.UserId!, result.OrganizationId!.Value);
    }
}
