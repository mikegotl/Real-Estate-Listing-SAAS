using System.Net;
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
    }
}
