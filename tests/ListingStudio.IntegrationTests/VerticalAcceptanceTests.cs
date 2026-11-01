using System.Diagnostics;
using ListingStudio.Application.Campaigns;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Properties;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ListingStudio.IntegrationTests;

/// <summary>
/// Prompt 12 acceptance: the four listing profiles the handoff names (standard, luxury or visually distinctive, agent
/// hosted footage, verified renovation story), and the operating cost of the new three-video default. Fake providers
/// only; nothing here calls a paid service.
/// </summary>
public sealed partial class CampaignGenerationTests
{
    private static readonly string[] UnconfirmedClaimWords =
        ["renovated", "remodeled", "exclusive", "off-market", "price drop", "motivated seller", "best neighborhood", "affordable"];

    [Fact]
    public async Task AcceptanceProfilesGetTheFormatsTheirVerifiedInputsSupportAndNothingElse()
    {
        var standard = await CreateOwnerAndPropertyAsync("accept-standard");
        var luxury = await CreateOwnerAndPropertyAsync("accept-luxury");
        var hosted = await CreateOwnerAndPropertyAsync("accept-hosted");
        var renovated = await CreateOwnerAndPropertyAsync("accept-renovated");
        var now = fixture.Factory.TimeProvider.GetUtcNow();
        Guid hostedVideo;
        Guid before;
        Guid after;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (PropertyMediaCategory Category, int Hero, int Quality)[] ordinary =
            [
                (PropertyMediaCategory.FrontExterior, 62, 76),
                (PropertyMediaCategory.Kitchen, 70, 78),
                (PropertyMediaCategory.LivingRoom, 64, 75),
                (PropertyMediaCategory.PrimaryBedroom, 52, 72),
                (PropertyMediaCategory.Backyard, 58, 70),
            ];
            foreach (var owner in new[] { standard, hosted })
            {
                for (var index = 0; index < ordinary.Length; index++)
                {
                    db.PropertyMedia.Add(AnalyzedMedia(owner, index, ordinary[index].Category, ordinary[index].Hero,
                        ordinary[index].Quality, now, $"Stop {index + 1}"));
                }
            }

            // Visually distinctive: an exceptional pool and view-led exterior on the storyboard.
            (PropertyMediaCategory Category, int Hero, int Quality)[] distinctive =
            [
                (PropertyMediaCategory.FrontExterior, 88, 90),
                (PropertyMediaCategory.Pool, 98, 94),
                (PropertyMediaCategory.Kitchen, 86, 90),
                (PropertyMediaCategory.LivingRoom, 84, 88),
                (PropertyMediaCategory.PrimaryBedroom, 80, 86),
                (PropertyMediaCategory.Backyard, 90, 88),
            ];
            for (var index = 0; index < distinctive.Length; index++)
            {
                db.PropertyMedia.Add(AnalyzedMedia(luxury, index, distinctive[index].Category, distinctive[index].Hero,
                    distinctive[index].Quality, now, $"Stop {index + 1}"));
            }

            // Agent-hosted footage: 40 s of processed walk-through with sound.
            var walk = PropertyVideo.Create(hosted.OrganizationId, hosted.PropertyId, $"tests/accept/{Guid.NewGuid():N}.mp4",
                "hosted.mp4", "video/mp4", 1_000, 1080, 1920, 40_000, 30m, true, 0);
            walk.BeginProcessing(now);
            walk.CompleteProcessing($"tests/accept/{Guid.NewGuid():N}-enhanced.mp4", 1_000, 1080, 1920, 40_000, 30m, "test", now);
            db.PropertyVideos.Add(walk);

            // Renovation: before and after kitchen photos, plus the rest of the storyboard.
            var beforeMedia = AnalyzedMedia(renovated, 0, PropertyMediaCategory.Kitchen, 30, 62, now, "Kitchen");
            var afterMedia = AnalyzedMedia(renovated, 1, PropertyMediaCategory.Kitchen, 85, 86, now, "Kitchen");
            db.PropertyMedia.AddRange(beforeMedia, afterMedia,
                AnalyzedMedia(renovated, 2, PropertyMediaCategory.LivingRoom, 72, 80, now, "Living room"),
                AnalyzedMedia(renovated, 3, PropertyMediaCategory.FrontExterior, 66, 78, now, "Exterior front"),
                AnalyzedMedia(renovated, 4, PropertyMediaCategory.Backyard, 60, 72, now, "Back yard"));
            await db.SaveChangesAsync();
            hostedVideo = walk.Id;
            before = beforeMedia.Id;
            after = afterMedia.Id;
        }

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var planning = scope.ServiceProvider.GetRequiredService<IVerticalCreativePlanningService>();
            var roles = scope.ServiceProvider.GetRequiredService<IVerticalMediaRoleService>();
            var claims = scope.ServiceProvider.GetRequiredService<IListingCreativeClaimService>();

            // The two optional formats need agent confirmation; before it, nothing implies a host or a renovation.
            var hostedBefore = await planning.PlanAsync(hosted.UserId, hosted.PropertyId, VerticalRecommendationContext.None);
            var renovatedBefore = await planning.PlanAsync(renovated.UserId, renovated.PropertyId, VerticalRecommendationContext.None);
            Assert.False(renovatedBefore!.Recommendation.For(VerticalCreativeFormat.StoryReveal).IsEligible);
            Assert.NotEmpty(renovatedBefore.Recommendation.For(VerticalCreativeFormat.StoryReveal).MissingInputs);
            Assert.DoesNotContain(renovatedBefore.Recipes, recipe => recipe.Format == VerticalCreativeFormat.StoryReveal);
            Assert.NotNull(hostedBefore);

            Assert.True(await roles.SetAsync(hosted.UserId, hosted.PropertyId, hostedVideo, VerticalMediaRole.HostedWalkthrough, VerticalMediaRoleDecision.Assigned));
            Assert.True(await claims.ConfirmStoryAsync(renovated.UserId, renovated.PropertyId,
                "The kitchen was closed off from the yard.", "Now it opens straight onto it."));
            Assert.True(await roles.SetAsync(renovated.UserId, renovated.PropertyId, before, VerticalMediaRole.BeforeRenovation, VerticalMediaRoleDecision.Assigned));
            Assert.True(await roles.SetAsync(renovated.UserId, renovated.PropertyId, after, VerticalMediaRole.AfterRenovation, VerticalMediaRoleDecision.Assigned));

            var plans = new Dictionary<string, VerticalCreativePlan>();
            foreach (var (name, owner) in new[] { ("standard", standard), ("luxury", luxury), ("hosted", hosted), ("renovated", renovated) })
            {
                var plan = await planning.PlanAsync(owner.UserId, owner.PropertyId, VerticalRecommendationContext.None);
                Assert.NotNull(plan);
                plans[name] = plan;

                // Every recommendation explains itself, every ineligible format says what is missing, and at most
                // three are recommended. Recipes exist only for eligible formats.
                Assert.InRange(plan.Recommendation.Recommended.Count, 1, VerticalFormatRules.MaximumRecommended);
                Assert.All(plan.Recommendation.Recommended, item => Assert.NotEmpty(item.Reasons));
                Assert.All(plan.Recommendation.Formats.Where(item => !item.IsEligible), item => Assert.NotEmpty(item.MissingInputs));
                Assert.All(plan.Recipes, recipe => Assert.True(plan.Recommendation.For(recipe.Format).IsEligible));

                // No format may say more than the agent confirmed.
                Assert.All(plan.Recipes, recipe =>
                {
                    foreach (var word in UnconfirmedClaimWords)
                    {
                        if (recipe.Format != VerticalCreativeFormat.StoryReveal)
                        {
                            Assert.DoesNotContain(word, recipe.Hook.Text, StringComparison.OrdinalIgnoreCase);
                        }
                    }
                });
                Assert.False(plan.Recommendation.For(VerticalCreativeFormat.ExclusiveAccess).IsEligible);
            }

            // Standard: the two core formats; no host, no story.
            Assert.Equal([VerticalCreativeFormat.WowFeature, VerticalCreativeFormat.PriceFirst],
                plans["standard"].Recommendation.Recommended.Select(item => item.Format).Order());

            // Visually distinctive: Wow Feature ranks first and scores above the standard listing's.
            Assert.Equal(VerticalCreativeFormat.WowFeature, plans["luxury"].Recommendation.Recommended[0].Format);
            Assert.True(plans["luxury"].Recommendation.For(VerticalCreativeFormat.WowFeature).Score
                > plans["standard"].Recommendation.For(VerticalCreativeFormat.WowFeature).Score);

            // Hosted footage: Hosted POV becomes eligible on the confirmed footage, built on that video.
            Assert.True(plans["hosted"].Recommendation.For(VerticalCreativeFormat.HostedPov).IsEligible);
            Assert.False(plans["standard"].Recommendation.For(VerticalCreativeFormat.HostedPov).IsEligible);

            // Verified renovation: the story uses the agent's words and opens on the before photo.
            var story = plans["renovated"].Recipes.Single(recipe => recipe.Format == VerticalCreativeFormat.StoryReveal);
            Assert.Equal("The kitchen was closed off from the yard.", story.Hook.Text);
            Assert.Equal(before, story.Beats[0].PropertyMediaId);
            Assert.Equal(after, story.PrimaryPropertyMediaId);
            Assert.All(plans["renovated"].Recipes.Where(recipe => recipe.Format != VerticalCreativeFormat.StoryReveal),
                recipe => Assert.DoesNotContain(recipe.Beats, beat => beat.PropertyMediaId == before));

            // Tenant isolation across the profiles: no owner can plan, tag or confirm on another's listing.
            Assert.Null(await planning.PlanAsync(standard.UserId, luxury.PropertyId, VerticalRecommendationContext.None));
            Assert.False(await roles.SetAsync(standard.UserId, hosted.PropertyId, hostedVideo, VerticalMediaRole.HostedWalkthrough, VerticalMediaRoleDecision.Removed));
            Assert.False(await claims.ConfirmStoryAsync(hosted.UserId, renovated.PropertyId, "Problem", "Payoff"));
        }
    }

    /// <summary>
    /// Measures one three-video default campaign end to end with fake providers: wall time, provider calls, stored
    /// bytes, stage retries and usage records. Writes the numbers to ACCEPTANCE_METRICS_PATH when it is set.
    /// </summary>
    [Fact]
    public async Task ThreeVideoDefaultCostsOneUsageRecordAndItsProviderCallsAreBounded()
    {
        var (admin, owner) = await CreateLibraryListingAsync("accept-measure");
        try
        {
            await SetLibrarySwitchAsync(admin, true, false, owner.OrganizationId);
            QueueLibraryProviders();
            var calls = (
                Voice: fixture.Factory.VoiceProvider.CallCount,
                Render: fixture.Factory.VideoRenderer.CallCount,
                Story: fixture.Factory.StoryGenerator.CallCount,
                Director: fixture.Factory.VideoDirector.CallCount);

            var clock = Stopwatch.StartNew();
            var queued = await EnqueueLibraryAsync(owner, VerticalVideoSelection.RecommendedThree);
            await ProcessUntilAsync(owner, queued.Id, CampaignGenerationStatus.Completed);
            clock.Stop();

            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var job = await db.CampaignGenerationJobs.AsNoTracking().SingleAsync(item => item.Id == queued.Id);
            var library = await db.CampaignVideoLibraries.AsNoTracking().Include(item => item.Variants)
                .SingleAsync(item => item.CampaignGenerationJobId == queued.Id);
            var deliverables = await db.CampaignDeliverables.AsNoTracking()
                .Where(item => item.CampaignGenerationJobId == queued.Id).ToListAsync();
            var videos = library.Variants.Count;
            var failed = library.Variants.Count(item => item.Status == CampaignVideoVariantStatus.Failed);
            var stored = deliverables.Sum(item => item.FileSize ?? 0);
            var voice = fixture.Factory.VoiceProvider.CallCount - calls.Voice;
            var render = fixture.Factory.VideoRenderer.CallCount - calls.Render;
            var story = fixture.Factory.StoryGenerator.CallCount - calls.Story;
            var director = fixture.Factory.VideoDirector.CallCount - calls.Director;
            var retries = job.TotalStageAttempts;

            Assert.Equal(1, await db.CampaignUsageRecords.CountAsync(item => item.CampaignGenerationJobId == queued.Id));
            Assert.Equal(0, failed);
            Assert.Equal(videos, render);                  // one render per video, no re-renders
            Assert.InRange(voice, 1, videos + 1);          // master voiceover plus at most one per narrated video
            Assert.Equal(1, story);                        // the story is written once and shared
            Assert.Equal(1, director);                     // so is the editorial plan
            Assert.True(stored > 0);

            var path = Environment.GetEnvironmentVariable("ACCEPTANCE_METRICS_PATH");
            if (!string.IsNullOrWhiteSpace(path))
            {
                await File.WriteAllTextAsync(path,
                    $"videos={videos}\nfailed={failed}\nwall_ms={clock.ElapsedMilliseconds}\nstage_attempts={retries}\n" +
                    $"render_calls={render}\nvoice_calls={voice}\nstory_calls={story}\ndirector_calls={director}\n" +
                    $"stored_bytes={stored}\nusage_records=1\n");
            }
        }
        finally
        {
            await SetLibrarySwitchAsync(admin, false, false);
        }
    }
}
