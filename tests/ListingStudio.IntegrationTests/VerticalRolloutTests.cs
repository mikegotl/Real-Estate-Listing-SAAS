using ListingStudio.Application.Administration;
using ListingStudio.Application.Campaigns;
using ListingStudio.Domain.Administration;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ListingStudio.IntegrationTests;

/// <summary>Prompt 12: flag-off equivalence on a database that already holds feature data, and the admin release switch page.</summary>
public sealed partial class CampaignGenerationTests
{
    [Fact]
    public async Task SwitchingOffWithFeatureDataKeepsEverythingStartsNothingAndReEnablingDuplicatesNothing()
    {
        var (admin, owner) = await CreateLibraryListingAsync("rollout-off");
        await GrantReviewAdminAsync(owner.UserId);
        await using var factory = fixture.CreateFactory(_ => { });
        try
        {
            // Feature data: a finished creative test (two library campaigns), a post with numbers and a draft report.
            await SetLibrarySwitchAsync(admin, true, true);
            QueueCreativeProviders(factory, campaigns: 2);
            Guid suiteId;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                suiteId = (await scope.ServiceProvider.GetRequiredService<IVerticalCreativeTestService>().StartAsync(
                    owner.UserId, owner.PropertyId, VerticalCreativeTestMode.HookAb, VerticalCreativeFormat.PriceFirst)).Id;
            }

            var suite = await RunCreativeSuiteAsync(factory, owner, suiteId);
            var video = suite.Cases[0].DeliverableId!.Value;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var performance = scope.ServiceProvider.GetRequiredService<IVerticalPerformanceService>();
                var published = factory.TimeProvider.GetUtcNow().AddHours(-1);
                var post = await performance.AddDistributionAsync(owner.UserId, video, DistributionPlatform.TikTok,
                    "https://www.tiktok.com/@agent/video/5001", published);
                await performance.AddSnapshotAsync(owner.UserId, post, new CampaignMetricValues(500, 40, 2, 3, 4, null, null, 6, 1, 0), published.AddMinutes(30));
                await scope.ServiceProvider.GetRequiredService<IVerticalRuleEvaluationService>().CreateDraftAsync(owner.UserId);
            }

            var before = await CountsAsync(factory, owner.OrganizationId);

            // Off: every feature entry point refuses, nothing new starts, finished work stays and still downloads.
            await SetLibrarySwitchAsync(admin, false, true);
            factory.TimeProvider.Advance(TimeSpan.FromSeconds(20));
            await using (var off = factory.Services.CreateAsyncScope())
            {
                var services = off.ServiceProvider;
                Assert.False(await services.GetRequiredService<ICampaignGenerationService>().IsVerticalLibraryEnabledAsync(owner.UserId));
                await Assert.ThrowsAsync<CreativeTestingDisabledException>(() => services.GetRequiredService<IVerticalCreativeTestService>().ListAsync(owner.UserId));
                await Assert.ThrowsAsync<PerformanceTrackingDisabledException>(() => services.GetRequiredService<IVerticalPerformanceService>()
                    .GetCampaignAsync(owner.UserId, suite.Cases[0].JobId!.Value));
                await Assert.ThrowsAsync<PerformanceTrackingDisabledException>(() => services.GetRequiredService<IVerticalRuleEvaluationService>()
                    .ListAsync(owner.UserId));

                var download = await services.GetRequiredService<ICampaignVideoLibraryService>().OpenVideoAsync(owner.UserId, video, null);
                Assert.NotNull(download);
                await using (download!.Content)
                {
                    Assert.True(download.Content.CanRead);
                }

                // The render regression suite stays usable.
                var regression = services.GetRequiredService<ITikTokTestSuiteService>();
                var started = await regression.StartAsync(owner.UserId, owner.PropertyId);
                await regression.CancelAsync(owner.UserId, started.Id);
            }

            for (var tick = 0; tick < 3; tick++)
            {
                await TickCreativeAsync(factory);
            }

            var whileOff = await CountsAsync(factory, owner.OrganizationId);
            Assert.Equal(before with { Jobs = whileOff.Jobs }, whileOff);
            // Only the regression suite's own campaign may have been added while off, never a library campaign.
            Assert.Equal(before.Libraries, whileOff.Libraries);

            // Back on: the same data reappears and the workers add nothing on their own.
            await SetLibrarySwitchAsync(admin, true, true);
            factory.TimeProvider.Advance(TimeSpan.FromSeconds(20));
            for (var tick = 0; tick < 3; tick++)
            {
                await TickCreativeAsync(factory);
            }

            Assert.Equal(whileOff, await CountsAsync(factory, owner.OrganizationId));
            await using var on = factory.Services.CreateAsyncScope();
            Assert.Equal(2, Assert.Single(await on.ServiceProvider.GetRequiredService<IVerticalCreativeTestService>().ListAsync(owner.UserId))
                .Cases.Count(item => item.Status == VerticalCreativeTestCaseStatus.Passed));
            Assert.Single(await on.ServiceProvider.GetRequiredService<IVerticalRuleEvaluationService>().ListAsync(owner.UserId));
        }
        finally
        {
            await SetLibrarySwitchAsync(admin, false, false);
        }
    }

    [Fact]
    public async Task ReleaseSwitchPageSuggestsTesterAndAdminWorkspacesAndAuditsEveryChange()
    {
        var (admin, owner) = await CreateLibraryListingAsync("rollout-page");
        var tester = await CreateOwnerAndPropertyAsync("rollout-tester");
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var roles = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.RoleManager<Microsoft.AspNetCore.Identity.IdentityRole>>();
            if (!await roles.RoleExistsAsync(PlatformTesters.Role))
            {
                Assert.True((await roles.CreateAsync(new Microsoft.AspNetCore.Identity.IdentityRole(PlatformTesters.Role))).Succeeded);
            }

            var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<ListingStudio.Infrastructure.Identity.ApplicationUser>>();
            Assert.True((await users.AddToRoleAsync((await users.FindByIdAsync(tester.UserId))!, PlatformTesters.Role)).Succeeded);
        }

        try
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var rollout = scope.ServiceProvider.GetRequiredService<IFeatureRolloutService>();
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => rollout.GetAsync(owner.UserId, FeatureFlagKeys.VerticalVideoLibraryV1));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => rollout.SaveAsync(owner.UserId, FeatureFlagKeys.VerticalVideoLibraryV1, true, true, []));

            await rollout.SaveAsync(admin, FeatureFlagKeys.VerticalVideoLibraryV1, false, false, []);
            var view = await rollout.GetAsync(admin, FeatureFlagKeys.VerticalVideoLibraryV1);
            Assert.StartsWith("Off for everyone", view.Summary, StringComparison.Ordinal);
            Assert.Contains(view.SuggestedPilots, item => item.Id == tester.OrganizationId && item.Reason == "Tester");
            var adminOrganization = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().OrganizationMembers.AsNoTracking()
                .Where(member => member.UserId == admin).Select(member => member.OrganizationId).SingleAsync();
            Assert.Contains(view.SuggestedPilots, item => item.Id == adminOrganization && item.Reason!.Contains("Platform admin", StringComparison.Ordinal));
            Assert.DoesNotContain(view.SuggestedPilots, item => item.Id == owner.OrganizationId);

            // Pilot for testers and admins first; the owner's workspace stays off.
            var saved = await rollout.SaveAsync(admin, FeatureFlagKeys.VerticalVideoLibraryV1, true, false, [tester.OrganizationId, adminOrganization]);
            Assert.Equal("On for 2 pilot workspaces.", saved.Summary);
            Assert.DoesNotContain(saved.SuggestedPilots, item => item.Id == tester.OrganizationId);
            var features = scope.ServiceProvider.GetRequiredService<IFeatureStateService>();
            Assert.True(await features.IsEnabledAsync(FeatureFlagKeys.VerticalVideoLibraryV1, tester.OrganizationId));
            Assert.False(await features.IsEnabledAsync(FeatureFlagKeys.VerticalVideoLibraryV1, owner.OrganizationId));

            var found = await rollout.SearchWorkspacesAsync(admin, "rollout-page Realty");
            Assert.Contains(found, item => item.Id == owner.OrganizationId);
            Assert.Empty(await rollout.SearchWorkspacesAsync(admin, "%"));
            await Assert.ThrowsAsync<ArgumentException>(() => rollout.SaveAsync(admin, FeatureFlagKeys.VerticalVideoLibraryV1, true, false, [Guid.NewGuid()]));

            var audit = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().PlatformAdminAuditEvents.AsNoTracking()
                .Where(item => item.Action == FeatureRolloutText.AuditAction).OrderBy(item => item.OccurredAtUtc).ToListAsync();
            Assert.True(audit.Count >= 2);
            Assert.Equal("global=1 all=0 pilots=2", audit[^1].Outcome);
            Assert.All(audit, item => Assert.Equal(FeatureFlagKeys.VerticalVideoLibraryV1, item.TargetReference));
        }
        finally
        {
            await SetLibrarySwitchAsync(admin, false, false);
        }
    }

    private sealed record FeatureCounts(int Jobs, int Libraries, int Variants, int Cases, int Posts, int Snapshots, int Reports, int UsageRecords, int Deliverables);

    private static async Task<FeatureCounts> CountsAsync(ListingStudioWebApplicationFactory factory, Guid organizationId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return new FeatureCounts(
            await db.CampaignGenerationJobs.CountAsync(item => item.OrganizationId == organizationId),
            await db.CampaignVideoLibraries.CountAsync(item => item.OrganizationId == organizationId),
            await db.CampaignVideoVariants.CountAsync(item => item.OrganizationId == organizationId),
            await db.VerticalCreativeTestCases.CountAsync(item => item.OrganizationId == organizationId),
            await db.CampaignDistributionRecords.CountAsync(item => item.OrganizationId == organizationId),
            await db.CampaignMetricSnapshots.CountAsync(item => item.OrganizationId == organizationId),
            await db.VerticalRuleEvaluationReports.CountAsync(item => item.OrganizationId == organizationId),
            await db.CampaignUsageRecords.CountAsync(item => item.OrganizationId == organizationId),
            await db.CampaignDeliverables.CountAsync(item => item.OrganizationId == organizationId && item.CampaignVideoVariantId != null));
    }
}
