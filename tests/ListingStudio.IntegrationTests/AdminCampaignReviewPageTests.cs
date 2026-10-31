using System.Net;
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
    }
}
