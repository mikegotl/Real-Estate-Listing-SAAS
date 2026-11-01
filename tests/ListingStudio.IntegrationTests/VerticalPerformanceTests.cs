using System.Text;
using ListingStudio.Application.Campaigns;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ListingStudio.IntegrationTests;

/// <summary>Prompt 10: posts, append-only metric snapshots, CSV import, comparisons, rule evaluation and the kill switch.</summary>
public sealed partial class CampaignGenerationTests
{
    [Fact]
    public async Task PerformanceIsRecordedPerLibraryVideoComparedWithinTheTestAndKeptWhenSwitchedOff()
    {
        var (admin, owner) = await CreateLibraryListingAsync("perf-track");
        await GrantReviewAdminAsync(owner.UserId);
        await using var factory = fixture.CreateFactory(_ => { });
        try
        {
            await SetLibrarySwitchAsync(admin, true, true);
            QueueCreativeProviders(factory, campaigns: 2);
            Guid suiteId;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                suiteId = (await scope.ServiceProvider.GetRequiredService<IVerticalCreativeTestService>().StartAsync(
                    owner.UserId, owner.PropertyId, VerticalCreativeTestMode.HookAb, VerticalCreativeFormat.PriceFirst)).Id;
            }

            var suite = await RunCreativeSuiteAsync(factory, owner, suiteId);
            var hookA = suite.Cases.Single(item => item.HookVariant == "a");
            var hookB = suite.Cases.Single(item => item.HookVariant == "b");
            var published = factory.TimeProvider.GetUtcNow().AddHours(-2);
            Guid postA, postB;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var service = scope.ServiceProvider.GetRequiredService<IVerticalPerformanceService>();
                Assert.True(await service.IsEnabledAsync(owner.UserId));
                postA = await service.AddDistributionAsync(owner.UserId, hookA.DeliverableId!.Value, DistributionPlatform.TikTok,
                    "https://www.tiktok.com/@agent/video/1001?is_from_webapp=1", published);
                postB = await service.AddDistributionAsync(owner.UserId, hookB.DeliverableId!.Value, DistributionPlatform.TikTok,
                    "https://www.tiktok.com/@agent/video/1002", published);

                // Only library videos in this workspace, only real platform links, each post once.
                await Assert.ThrowsAsync<KeyNotFoundException>(() => service.AddDistributionAsync(owner.UserId, Guid.NewGuid(),
                    DistributionPlatform.TikTok, "https://www.tiktok.com/@agent/video/1003", published));
                await Assert.ThrowsAsync<ArgumentException>(() => service.AddDistributionAsync(owner.UserId, hookA.DeliverableId!.Value,
                    DistributionPlatform.TikTok, "https://example.com/video/1", published));
                await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddDistributionAsync(owner.UserId, hookA.DeliverableId!.Value,
                    DistributionPlatform.TikTok, "https://www.tiktok.com/@agent/video/1001", published));

                // Hook A: more views and likes. Hook B: fewer likes but more leads.
                await service.AddSnapshotAsync(owner.UserId, postA, new CampaignMetricValues(1_000, 90, 5, 4, 3, 9.5m, 0.30m, 10, 1, 0),
                    published.AddMinutes(30));
                await service.AddSnapshotAsync(owner.UserId, postA, new CampaignMetricValues(4_000, 360, 12, 20, 15, 10m, 0.32m, 40, 1, 0),
                    published.AddMinutes(90));
                await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddSnapshotAsync(owner.UserId, postA,
                    new CampaignMetricValues(5_000, 1, 1, 1, 1, null, null, 1, 0, 0), published.AddMinutes(90).AddMilliseconds(400)));
                await Assert.ThrowsAsync<ArgumentException>(() => service.AddSnapshotAsync(owner.UserId, postA,
                    new CampaignMetricValues(10, 20, 0, 0, 0, null, null, 0, 0, 0), published.AddMinutes(100)));
                await Assert.ThrowsAsync<ArgumentException>(() => service.AddSnapshotAsync(owner.UserId, postA,
                    new CampaignMetricValues(10, 1, 0, 0, 0, null, null, 0, 0, 0), published.AddMinutes(-5)));

                // CSV: one bad row means nothing is imported, and every problem names its line.
                var bad = Csv(
                    "TikTok,https://www.tiktok.com/@agent/video/1002," + Stamp(published.AddMinutes(60)) + ",2000,100,4,6,8,,25%,60,5,2",
                    "TikTok,https://www.tiktok.com/@agent/video/9999," + Stamp(published.AddMinutes(60)) + ",10,1,0,0,0,,,0,0,0",
                    "TikTok,https://www.tiktok.com/@agent/video/1002," + Stamp(published.AddMinutes(61)) + ",10,50,0,0,0,,,0,0,0");
                var rejected = await service.ImportCsvAsync(owner.UserId, bad);
                Assert.False(rejected.Succeeded);
                Assert.Equal(0, rejected.Imported);
                Assert.Contains(rejected.Errors, line => line.StartsWith("Line 3:", StringComparison.Ordinal));
                Assert.Contains(rejected.Errors, line => line.StartsWith("Line 4:", StringComparison.Ordinal));
                var good = Csv(
                    "TikTok,https://www.tiktok.com/@agent/video/1002/," + Stamp(published.AddMinutes(60)) + ",2000,100,4,6,8,,25%,60,5,2");
                var imported = await service.ImportCsvAsync(owner.UserId, good);
                Assert.True(imported.Succeeded, string.Join(" | ", imported.Errors));
                Assert.Equal(1, imported.Imported);
                // The same reading again is a duplicate.
                Assert.False((await service.ImportCsvAsync(owner.UserId, Csv(
                    "TikTok,https://www.tiktok.com/@agent/video/1002," + Stamp(published.AddMinutes(60)) + ",2000,100,4,6,8,,25%,60,5,2"))).Succeeded);

                var campaign = (await service.GetCampaignAsync(owner.UserId, hookA.JobId!.Value))!;
                var onlyVideo = Assert.Single(campaign.Videos);
                Assert.Equal(4_000, onlyVideo.Totals.Views);
                Assert.Equal(0.09m, onlyVideo.Rates.LikeRate);
                Assert.Equal(VerticalPerformanceCalculator.CalculationVersion, campaign.CalculationVersion);
            }

            // Snapshots are append-only in the database itself.
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(
                    "UPDATE \"CampaignMetricSnapshots\" SET \"Views\" = 1 WHERE \"DistributionRecordId\" = {0}", postA));
                await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(
                    "DELETE FROM \"CampaignMetricSnapshots\" WHERE \"DistributionRecordId\" = {0}", postA));
                Assert.Equal(3, await db.CampaignMetricSnapshots.CountAsync(snapshot => snapshot.DistributionRecordId == postA || snapshot.DistributionRecordId == postB));
            }

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var evaluation = scope.ServiceProvider.GetRequiredService<IVerticalRuleEvaluationService>();
                var test = Assert.Single(await evaluation.GetSuitePerformanceAsync(owner.UserId));
                Assert.Equal(suiteId, test.SuiteId);
                var a = test.Videos.Single(video => video.HookVariant == "a");
                var b = test.Videos.Single(video => video.HookVariant == "b");
                Assert.True(a.IsAwarenessLeader);
                Assert.False(a.IsBusinessLeader);
                Assert.True(b.IsBusinessLeader);
                Assert.Equal(0.05m, b.Rates.LikeRate);
                Assert.Equal(0.25m, b.Rates.CompletionRate);
                Assert.Equal(4m, b.Uplift.Leads);

                var draft = await evaluation.CreateDraftAsync(owner.UserId);
                Assert.Equal(2, draft.VideoCount);
                Assert.Equal(6_000, draft.TotalViews);
                Assert.False(draft.MeetsMinimumSample);
                Assert.Contains(draft.Segments, segment => segment.Dimension == "Agent account" && segment.Value == "Agent 1");
                Assert.DoesNotContain(draft.Segments, segment => segment.Value.Contains('@', StringComparison.Ordinal));
                await Assert.ThrowsAsync<InvalidOperationException>(() => evaluation.PromoteAsync(owner.UserId, draft.Id, VerticalFormatRules.Version));
                await Assert.ThrowsAsync<InvalidOperationException>(() => evaluation.PromoteAsync(owner.UserId, draft.Id, "vertical-rules-v2"));
                Assert.Equal(VerticalFormatRules.Version, await evaluation.GetActiveRuleVersionAsync(owner.UserId));

                // A render regression campaign never feeds the rule evaluation, even with numbers recorded against it.
                var regression = scope.ServiceProvider.GetRequiredService<ITikTokTestSuiteService>();
                var regressionSuite = await regression.StartAsync(owner.UserId, owner.PropertyId);
                await regression.CancelAsync(owner.UserId, regressionSuite.Id);
                await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.ExecuteSqlRawAsync(
                    "UPDATE \"TikTokTestSuites\" SET \"ShortVoiceoverJobId\" = {0} WHERE \"Id\" = {1}", hookA.JobId!.Value, regressionSuite.Id);
                var withoutRegression = await evaluation.CreateDraftAsync(owner.UserId);
                Assert.Equal(1, withoutRegression.VideoCount);
                Assert.Equal(2_000, withoutRegression.TotalViews);

                // Another workspace's admin sees none of it and can't write to it.
                Assert.Empty(await evaluation.GetSuitePerformanceAsync(admin));
                Assert.Equal(0, (await evaluation.CreateDraftAsync(admin)).VideoCount);
                await Assert.ThrowsAsync<KeyNotFoundException>(() => evaluation.PromoteAsync(admin, draft.Id, VerticalFormatRules.Version));
                var service = scope.ServiceProvider.GetRequiredService<IVerticalPerformanceService>();
                Assert.Null(await service.GetCampaignAsync(admin, hookA.JobId!.Value));
                await Assert.ThrowsAsync<KeyNotFoundException>(() => service.AddSnapshotAsync(admin, postA,
                    new CampaignMetricValues(1, 0, 0, 0, 0, null, null, 0, 0, 0), factory.TimeProvider.GetUtcNow()));
                await Assert.ThrowsAsync<KeyNotFoundException>(() => service.AddDistributionAsync(admin, hookA.DeliverableId!.Value,
                    DistributionPlatform.TikTok, "https://www.tiktok.com/@other/video/1", published));
                Assert.False((await service.ImportCsvAsync(admin, Csv(
                    "TikTok,https://www.tiktok.com/@agent/video/1002," + Stamp(published.AddMinutes(70)) + ",1,0,0,0,0,,,0,0,0"))).Succeeded);
            }

            // Kill switch: every call refused, nothing deleted, everything back when it's turned on.
            await SetLibrarySwitchAsync(admin, false, false);
            factory.TimeProvider.Advance(TimeSpan.FromSeconds(20));
            await using (var off = factory.Services.CreateAsyncScope())
            {
                var service = off.ServiceProvider.GetRequiredService<IVerticalPerformanceService>();
                Assert.False(await service.IsEnabledAsync(owner.UserId));
                await Assert.ThrowsAsync<PerformanceTrackingDisabledException>(() => service.GetCampaignAsync(owner.UserId, hookA.JobId!.Value));
                await Assert.ThrowsAsync<PerformanceTrackingDisabledException>(() => service.AddSnapshotAsync(owner.UserId, postA,
                    new CampaignMetricValues(1, 0, 0, 0, 0, null, null, 0, 0, 0), factory.TimeProvider.GetUtcNow()));
                await Assert.ThrowsAsync<PerformanceTrackingDisabledException>(() => service.ImportCsvAsync(owner.UserId, Csv()));
                await Assert.ThrowsAsync<PerformanceTrackingDisabledException>(() =>
                    off.ServiceProvider.GetRequiredService<IVerticalRuleEvaluationService>().CreateDraftAsync(owner.UserId));
                var db = off.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                Assert.Equal(2, await db.CampaignDistributionRecords.CountAsync(record => record.PropertyId == owner.PropertyId));
                Assert.Equal(3, await db.CampaignMetricSnapshots.CountAsync(snapshot => snapshot.OrganizationId == owner.OrganizationId));
            }

            await SetLibrarySwitchAsync(admin, true, true);
            factory.TimeProvider.Advance(TimeSpan.FromSeconds(20));
            await using var on = factory.Services.CreateAsyncScope();
            Assert.Single((await on.ServiceProvider.GetRequiredService<IVerticalPerformanceService>()
                .GetCampaignAsync(owner.UserId, hookB.JobId!.Value))!.Videos, video => video.HasMetrics);
        }
        finally
        {
            await SetLibrarySwitchAsync(admin, false, false);
        }

        static string Stamp(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

        static MemoryStream Csv(params string[] rows) => new(Encoding.UTF8.GetBytes(
            "platform,post_url,captured_at,views,likes,comments,shares,saves,average_watch_seconds,completion_rate,clicks,leads,booked_showings\n"
            + string.Join("\n", rows)));
    }

    [Fact]
    public async Task OnlyPlatformAdminsRunTheRuleEvaluation()
    {
        var (admin, owner) = await CreateLibraryListingAsync("perf-roles");
        await using var factory = fixture.CreateFactory(_ => { });
        try
        {
            await SetLibrarySwitchAsync(admin, true, true);
            await using var scope = factory.Services.CreateAsyncScope();
            var evaluation = scope.ServiceProvider.GetRequiredService<IVerticalRuleEvaluationService>();
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => evaluation.CreateDraftAsync(owner.UserId));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => evaluation.GetSuitePerformanceAsync(owner.UserId));
            Assert.True(await scope.ServiceProvider.GetRequiredService<IVerticalPerformanceService>().IsEnabledAsync(owner.UserId));
        }
        finally
        {
            await SetLibrarySwitchAsync(admin, false, false);
        }
    }
}
