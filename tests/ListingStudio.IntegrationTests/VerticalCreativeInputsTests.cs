using ListingStudio.Application.Campaigns;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Properties;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed partial class CampaignGenerationTests
{
    [Fact]
    public async Task AgentMediaRolesWinOverSuggestionsKeepHistoryAndStayInTheirWorkspace()
    {
        var owner = await CreateOwnerAndPropertyAsync("roles-owner");
        var outsider = await CreateOwnerAndPropertyAsync("roles-outsider");
        var now = fixture.Factory.TimeProvider.GetUtcNow();
        Guid kitchen;
        Guid patio;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var kitchenMedia = AnalyzedMedia(owner, 0, PropertyMediaCategory.Kitchen, 90, 86, now, "Kitchen");
            var patioMedia = AnalyzedMedia(owner, 1, PropertyMediaCategory.Patio, 60, 75, now, "Back patio");
            db.PropertyMedia.AddRange(kitchenMedia, patioMedia,
                AnalyzedMedia(owner, 2, PropertyMediaCategory.LivingRoom, 62, 78, now, "Living room"));
            await db.SaveChangesAsync();
            kitchen = kitchenMedia.Id;
            patio = patioMedia.Id;
        }

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var roles = scope.ServiceProvider.GetRequiredService<IVerticalMediaRoleService>();
            var before = await roles.GetAsync(owner.UserId, owner.PropertyId);
            Assert.Contains(new VerticalMediaRoleState(VerticalMediaRole.HeroFeature, VerticalMediaRoleSource.Suggested),
                before!.Single(item => item.TargetId == kitchen).Roles);

            // The agent moves the hero to the patio: the patio's choice is the agent's and the kitchen's suggestion goes.
            Assert.True(await roles.SetAsync(owner.UserId, owner.PropertyId, patio, VerticalMediaRole.HeroFeature, VerticalMediaRoleDecision.Assigned));
            fixture.Factory.TimeProvider.Advance(TimeSpan.FromSeconds(1));
            Assert.True(await roles.SetAsync(owner.UserId, owner.PropertyId, kitchen, VerticalMediaRole.HeroFeature, VerticalMediaRoleDecision.Removed));
            fixture.Factory.TimeProvider.Advance(TimeSpan.FromSeconds(1));
            Assert.True(await roles.SetAsync(owner.UserId, owner.PropertyId, kitchen, VerticalMediaRole.AfterRenovation, VerticalMediaRoleDecision.Assigned));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                roles.SetAsync(owner.UserId, owner.PropertyId, kitchen, VerticalMediaRole.HostedWalkthrough, VerticalMediaRoleDecision.Assigned));

            var after = await roles.GetAsync(owner.UserId, owner.PropertyId);
            Assert.Contains(new VerticalMediaRoleState(VerticalMediaRole.HeroFeature, VerticalMediaRoleSource.Agent),
                after!.Single(item => item.TargetId == patio).Roles);
            Assert.DoesNotContain(after!.Single(item => item.TargetId == kitchen).Roles, state => state.Role == VerticalMediaRole.HeroFeature);
            Assert.Contains(new VerticalMediaRoleState(VerticalMediaRole.AfterRenovation, VerticalMediaRoleSource.Agent),
                after!.Single(item => item.TargetId == kitchen).Roles);

            // Going back to the suggestion is one more row, not an edit.
            fixture.Factory.TimeProvider.Advance(TimeSpan.FromSeconds(1));
            Assert.True(await roles.SetAsync(owner.UserId, owner.PropertyId, kitchen, VerticalMediaRole.HeroFeature, VerticalMediaRoleDecision.Cleared));
            var history = await roles.GetHistoryAsync(owner.UserId, owner.PropertyId);
            Assert.Equal(4, history.Count);
            Assert.Equal(VerticalMediaRoleDecision.Cleared, history[0].Decision);
            Assert.All(history, entry => Assert.Equal(owner.UserId, entry.SetByUserId));

            // Another workspace can neither read nor change these roles.
            Assert.Null(await roles.GetAsync(outsider.UserId, owner.PropertyId));
            Assert.False(await roles.SetAsync(outsider.UserId, owner.PropertyId, kitchen, VerticalMediaRole.HeroFeature, VerticalMediaRoleDecision.Assigned));
            Assert.False(await roles.SetAsync(outsider.UserId, outsider.PropertyId, kitchen, VerticalMediaRole.HeroFeature, VerticalMediaRoleDecision.Assigned));
            Assert.Empty(await roles.GetHistoryAsync(outsider.UserId, owner.PropertyId));
        }

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            // Deleting a photo removes its role history with it.
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.PropertyMedia.Where(media => media.Id == kitchen).ExecuteDeleteAsync();
            Assert.False(await db.PropertyMediaRoleAssignments.AnyAsync(assignment => assignment.PropertyMediaId == kitchen));
            Assert.True(await db.PropertyMediaRoleAssignments.AnyAsync(assignment => assignment.PropertyMediaId == patio));
        }
    }

    [Fact]
    public async Task ConfirmedClaimsUnlockStoryAndAccessFormatsUntilWithdrawnOrExpired()
    {
        var owner = await CreateOwnerAndPropertyAsync("claims-owner");
        var outsider = await CreateOwnerAndPropertyAsync("claims-outsider");
        var now = fixture.Factory.TimeProvider.GetUtcNow();
        Guid before;
        Guid after;
        Guid video;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var beforeMedia = AnalyzedMedia(owner, 0, PropertyMediaCategory.Kitchen, 30, 62, now, "Kitchen");
            var afterMedia = AnalyzedMedia(owner, 1, PropertyMediaCategory.Kitchen, 85, 86, now, "Kitchen");
            db.PropertyMedia.AddRange(beforeMedia, afterMedia,
                AnalyzedMedia(owner, 2, PropertyMediaCategory.LivingRoom, 72, 80, now, "Living room"),
                AnalyzedMedia(owner, 3, PropertyMediaCategory.FrontExterior, 66, 78, now, "Exterior front"));
            var clip = PropertyVideo.Create(owner.OrganizationId, owner.PropertyId, $"tests/claims/{Guid.NewGuid():N}.mp4",
                "walk.mp4", "video/mp4", 1_000, 1920, 1080, 30_000, 30m, true, 0);
            clip.BeginProcessing(now);
            clip.CompleteProcessing($"tests/claims/{Guid.NewGuid():N}-enhanced.mp4", 1_000, 1080, 1920, 30_000, 30m, "test", now);
            db.PropertyVideos.Add(clip);
            await db.SaveChangesAsync();
            before = beforeMedia.Id;
            after = afterMedia.Id;
            video = clip.Id;
        }

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var claims = scope.ServiceProvider.GetRequiredService<IListingCreativeClaimService>();
            var roles = scope.ServiceProvider.GetRequiredService<IVerticalMediaRoleService>();
            var planning = scope.ServiceProvider.GetRequiredService<IVerticalCreativePlanningService>();

            var plain = await planning.PlanAsync(owner.UserId, owner.PropertyId, VerticalRecommendationContext.None);
            Assert.False(plain!.Recommendation.For(VerticalCreativeFormat.StoryReveal).IsEligible);
            Assert.False(plain.Recommendation.For(VerticalCreativeFormat.ExclusiveAccess).IsEligible);
            Assert.DoesNotContain(plain.Recipes, recipe => recipe.Format == VerticalCreativeFormat.StoryReveal);

            Assert.True(await claims.ConfirmStoryAsync(owner.UserId, owner.PropertyId, "The kitchen was closed off.", "Now it opens to the yard."));
            Assert.True(await claims.ConfirmAccessAsync(owner.UserId, owner.PropertyId, "Private preview Saturday", now.AddDays(3)));
            Assert.True(await roles.SetAsync(owner.UserId, owner.PropertyId, before, VerticalMediaRole.BeforeRenovation, VerticalMediaRoleDecision.Assigned));
            Assert.True(await roles.SetAsync(owner.UserId, owner.PropertyId, after, VerticalMediaRole.AfterRenovation, VerticalMediaRoleDecision.Assigned));
            Assert.False(await claims.ConfirmStoryAsync(outsider.UserId, owner.PropertyId, "Problem", "Payoff"));
            Assert.Null(await claims.GetActiveAsync(outsider.UserId, owner.PropertyId));

            var confirmed = await planning.PlanAsync(owner.UserId, owner.PropertyId, VerticalRecommendationContext.None);
            Assert.True(confirmed!.Recommendation.For(VerticalCreativeFormat.StoryReveal).IsEligible);
            Assert.True(confirmed.Recommendation.For(VerticalCreativeFormat.ExclusiveAccess).IsEligible);
            var story = confirmed.Recipes.Single(recipe => recipe.Format == VerticalCreativeFormat.StoryReveal);
            Assert.Equal("The kitchen was closed off.", story.Hook.Text);
            Assert.Equal(before, story.Beats[0].PropertyMediaId);
            Assert.Equal(after, story.PrimaryPropertyMediaId);
            var access = confirmed.Recipes.Single(recipe => recipe.Format == VerticalCreativeFormat.ExclusiveAccess);
            Assert.Equal("Private preview Saturday", access.Hook.Text);
            Assert.Equal(video, access.PrimaryPropertyVideoId);
            Assert.All(confirmed.Recipes.Where(recipe => recipe.Format != VerticalCreativeFormat.StoryReveal),
                recipe => Assert.DoesNotContain(recipe.Beats, beat => beat.PropertyMediaId == before));
            Assert.All(confirmed.Readiness, readiness =>
                Assert.Equal(confirmed.Recommendation.For(readiness.Format).IsEligible, readiness.IsReady));

            // A withdrawal is a new row; the story is ineligible again but the confirmation is still on record.
            Assert.True(await claims.WithdrawAsync(owner.UserId, owner.PropertyId, ListingCreativeClaimKind.StoryReveal));
            var active = await claims.GetActiveAsync(owner.UserId, owner.PropertyId);
            Assert.Equal([ListingCreativeClaimKind.ExclusiveAccess], active!.Select(claim => claim.Kind));
            var withdrawn = await planning.PlanAsync(owner.UserId, owner.PropertyId, VerticalRecommendationContext.None);
            Assert.False(withdrawn!.Recommendation.For(VerticalCreativeFormat.StoryReveal).IsEligible);
        }

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Equal(3, await db.ListingCreativeClaims.CountAsync(claim => claim.PropertyId == owner.PropertyId
                && claim.OrganizationId == owner.OrganizationId));

            // Access language past its date no longer counts. Evaluated with a later clock, without changing the shared one.
            var later = new VerticalFormatRecommendationServiceAt(db, now.AddDays(4));
            var expired = await later.PlanForOrganizationAsync(owner.OrganizationId, owner.PropertyId, VerticalRecommendationContext.None);
            Assert.False(expired!.Recommendation.For(VerticalCreativeFormat.ExclusiveAccess).IsEligible);
            Assert.Null(await later.PlanForOrganizationAsync(outsider.OrganizationId, owner.PropertyId, VerticalRecommendationContext.None));
        }
    }

    private sealed class VerticalFormatRecommendationServiceAt(ApplicationDbContext db, DateTimeOffset now)
    {
        private readonly ListingStudio.Infrastructure.Campaigns.VerticalFormatRecommendationService inner =
            new(db, new FixedClock(now));

        public Task<VerticalCreativePlan?> PlanForOrganizationAsync(Guid organizationId, Guid propertyId, VerticalRecommendationContext context) =>
            inner.PlanForOrganizationAsync(organizationId, propertyId, context);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
