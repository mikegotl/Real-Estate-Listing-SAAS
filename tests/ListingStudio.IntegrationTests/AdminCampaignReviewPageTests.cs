using System.Net;
using ListingStudio.Domain.Administration;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Headers;
using ListingStudio.Application.Administration;
using ListingStudio.Application.Authentication;
using ListingStudio.Infrastructure.Administration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed class AdminCampaignReviewPageTests(PostgreSqlWebApplicationFixture fixture)
    : IClassFixture<PostgreSqlWebApplicationFixture>
{
    [Fact]
    public async Task GalleryRequiresPlatformRoleAndRendersCurrentWorkspaceEmptyState()
    {
        await using var factory = fixture.CreateFactory(services =>
        {
            services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = PlatformAdminHeaderAuthenticationHandler.SchemeName;
                    options.DefaultChallengeScheme = PlatformAdminHeaderAuthenticationHandler.SchemeName;
                    options.DefaultForbidScheme = PlatformAdminHeaderAuthenticationHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, PlatformAdminHeaderAuthenticationHandler>(
                    PlatformAdminHeaderAuthenticationHandler.SchemeName, _ => { });
        });
        await using var scope = factory.Services.CreateAsyncScope();
        var email = $"campaign-review-page-{Guid.NewGuid():N}@example.com";
        var registered = await scope.ServiceProvider.GetRequiredService<IAccountRegistrationService>()
            .RegisterAsync(new RegisterAccountCommand(email, "Password123", $"Review workspace {Guid.NewGuid():N}"));
        Assert.True(registered.Succeeded);

        using var client = factory.CreateClient();
        using var anonymous = await client.GetAsync("/admin/campaign-tests");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            PlatformAdminHeaderAuthenticationHandler.SchemeName, registered.UserId);
        using var owner = await client.GetAsync("/admin/campaign-tests");

        Assert.True(await scope.ServiceProvider.GetRequiredService<PlatformAdminRoleBootstrapper>().GrantAsync(email));
        client.DefaultRequestHeaders.Add(PlatformAdminHeaderAuthenticationHandler.RoleHeader, PlatformAdministration.Role);
        using var admin = await client.GetAsync("/admin/campaign-tests");
        var content = await admin.Content.ReadAsStringAsync();
        using var dashboard = await client.GetAsync("/admin");
        var dashboardContent = await dashboard.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, owner.StatusCode);
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
        Assert.Contains("TikTok review studio", content, StringComparison.Ordinal);
        Assert.Contains("No TikTok campaigns yet", content, StringComparison.Ordinal);
        Assert.Contains("your workspace only", content, StringComparison.Ordinal);
        Assert.Contains("Start three TikTok tests", content, StringComparison.Ordinal);
        Assert.Contains("generation credits and campaign allowance", content, StringComparison.Ordinal);
        Assert.Contains("A replacement render needs a fresh review", content, StringComparison.Ordinal);
        Assert.Contains("/admin/campaign-tests", dashboardContent, StringComparison.Ordinal);

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var org = await db.OrganizationMembers.Where(m => m.UserId == registered.UserId)
            .Select(m => m.OrganizationId).SingleAsync();
        var now = DateTimeOffset.UtcNow;
        var older = TikTokTestSuite.Create(org, Guid.NewGuid(), registered.UserId!, now.AddMinutes(-1));
        older.Stop(true, "Cancelled", now);
        var newest = TikTokTestSuite.Create(org, Guid.NewGuid(), registered.UserId!, now);
        db.TikTokTestSuites.AddRange(older, newest);
        await db.SaveChangesAsync();

        var grouped = await client.GetStringAsync("/admin/campaign-tests");
        // IDs also appear in aria-controls and section-body IDs, independent of HTML text encoding.
        Assert.True(grouped.IndexOf($"session-body-{newest.Id}", StringComparison.Ordinal)
            < grouped.IndexOf($"session-body-{older.Id}", StringComparison.Ordinal));
        Assert.Contains("Cancel session", grouped, StringComparison.Ordinal);
        Assert.Contains("Archive session", grouped, StringComparison.Ordinal);
        Assert.Contains("Updates automatically every 5 seconds", grouped, StringComparison.Ordinal);
        Assert.Contains("aria-expanded=\"true\"", grouped, StringComparison.Ordinal);
        Assert.Contains("aria-expanded=\"false\"", grouped, StringComparison.Ordinal);

        older.SetArchived(true, now);
        await db.SaveChangesAsync();
        var archivedPage = await client.GetStringAsync("/admin/campaign-tests");
        Assert.Contains("Show archived sessions", archivedPage, StringComparison.Ordinal);
        Assert.DoesNotContain($"session-body-{older.Id}", archivedPage, StringComparison.Ordinal);
        older.SetArchived(false, now);
        await db.SaveChangesAsync();
        var restoredPage = await client.GetStringAsync("/admin/campaign-tests");
        Assert.Contains($"session-body-{older.Id}", restoredPage, StringComparison.Ordinal);
        newest.Stop(true, "Test cleanup", now);
        await db.SaveChangesAsync();

        // With VerticalVideoLibraryV1 on, the page becomes the Creative Testing Studio with two tabs; the creative tab
        // is first and the regression gallery is one tab away. Off again, it is the regression page exactly as before.
        var features = scope.ServiceProvider.GetRequiredService<IFeatureStateService>();
        await features.ConfigureAsync(registered.UserId!, FeatureFlagKeys.VerticalVideoLibraryV1, true, true, []);
        factory.TimeProvider.Advance(TimeSpan.FromSeconds(20));
        var studio = await client.GetStringAsync("/admin/campaign-tests");
        Assert.Contains("TikTok Creative Testing Studio", studio, StringComparison.Ordinal);
        Assert.Contains("data-testid=\"tab-creative\"", studio, StringComparison.Ordinal);
        Assert.Contains("data-testid=\"tab-regression\"", studio, StringComparison.Ordinal);
        Assert.Contains("data-testid=\"creative-tab\"", studio, StringComparison.Ordinal);
        Assert.Contains("Awaiting creative review", studio, StringComparison.Ordinal);
        Assert.DoesNotContain("Start three TikTok tests", studio, StringComparison.Ordinal);
        var regressionTab = await client.GetStringAsync("/admin/campaign-tests?tab=regression");
        Assert.Contains("Start three TikTok tests", regressionTab, StringComparison.Ordinal);
        Assert.DoesNotContain("data-testid=\"creative-tab\"", regressionTab, StringComparison.Ordinal);

        await features.ConfigureAsync(registered.UserId!, FeatureFlagKeys.VerticalVideoLibraryV1, false, false, []);
        factory.TimeProvider.Advance(TimeSpan.FromSeconds(20));
        var switchedOff = await client.GetStringAsync("/admin/campaign-tests?tab=creative");
        Assert.Contains("TikTok review studio", switchedOff, StringComparison.Ordinal);
        Assert.Contains("Start three TikTok tests", switchedOff, StringComparison.Ordinal);
        Assert.DoesNotContain("Creative format testing", switchedOff, StringComparison.Ordinal);
        Assert.DoesNotContain("creative-tab", switchedOff, StringComparison.Ordinal);
    }
}
