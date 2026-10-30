using ListingStudio.Application.Administration;
using ListingStudio.Application.Campaigns;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Infrastructure.Identity;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed partial class CampaignGenerationTests
{
    [Fact]
    public async Task CampaignReviewRequiresPlatformRoleAndNeverBypassesOrganizationScope()
    {
        var owner = await CreateOwnerAndPropertyAsync("review-owner");
        var outsider = await CreateOwnerAndPropertyAsync("review-outsider");
        await UploadAsync(owner);
        QueueSuccessfulProviders();
        fixture.Factory.VoiceProvider.Enqueue(CreateSegmentedVoiceResult);
        var queued = await EnqueueAsync(owner, CampaignOutputSelection.TikTok);
        await ProcessUntilAsync(owner, queued.Id, CampaignGenerationStatus.Completed);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ICampaignReviewService>();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ListAsync(owner.UserId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.DecideAsync(owner.UserId,
            queued.Id, 1, CampaignVideoReviewDecision.Approved, null));

        await GrantReviewAdminAsync(owner.UserId);
        await GrantReviewAdminAsync(outsider.UserId);
        var item = Assert.Single(await service.ListAsync(owner.UserId));
        Assert.Equal(queued.Id, item.JobId);
        Assert.Null(item.Decision);
        Assert.Empty(await service.ListAsync(outsider.UserId));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.DecideAsync(outsider.UserId,
            queued.Id, 1, CampaignVideoReviewDecision.Approved, null));
    }

    [Fact]
    public async Task ReviewIsPersistedForExactVersionAndReplacementNeedsFreshApproval()
    {
        var owner = await CreateOwnerAndPropertyAsync("review-version");
        await GrantReviewAdminAsync(owner.UserId);
        await UploadAsync(owner);
        QueueSuccessfulProviders();
        fixture.Factory.VoiceProvider.Enqueue(CreateSegmentedVoiceResult);
        var queued = await EnqueueAsync(owner, CampaignOutputSelection.TikTok);
        await ProcessUntilAsync(owner, queued.Id, CampaignGenerationStatus.Completed);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ICampaignReviewService>();
        await service.DecideAsync(owner.UserId, queued.Id, 1, CampaignVideoReviewDecision.Approved, "Ready");
        var approved = Assert.Single(await service.ListAsync(owner.UserId));
        Assert.Equal(CampaignVideoReviewDecision.Approved, approved.Decision);
        Assert.Equal("Ready", Assert.Single(approved.History).Feedback);

        // The fixture clock is frozen: insertion order must still select the newest decision reliably.
        await service.DecideAsync(owner.UserId, queued.Id, 1, CampaignVideoReviewDecision.ChangesRequested, "Adjust the framing.");
        var requested = Assert.Single(await service.ListAsync(owner.UserId));
        Assert.Equal(CampaignVideoReviewDecision.ChangesRequested, requested.Decision);
        Assert.Equal("Adjust the framing.", requested.History[0].Feedback);
        await service.DecideAsync(owner.UserId, queued.Id, 1, CampaignVideoReviewDecision.Approved, "Framing accepted.");
        Assert.Equal(CampaignVideoReviewDecision.Approved, Assert.Single(await service.ListAsync(owner.UserId)).Decision);

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var video = await db.CampaignDeliverables.SingleAsync(candidate => candidate.CampaignGenerationJobId == queued.Id);
        video.ApplyRevision(video.SpecificationJson, video.VideoNarrationId, $"review-{Guid.NewGuid():N}.mp4",
            "video/mp4", 6, 2, fixture.Factory.TimeProvider.GetUtcNow());
        await db.SaveChangesAsync();
        var replacement = Assert.Single(await service.ListAsync(owner.UserId));
        Assert.Equal(2, replacement.Version);
        Assert.Null(replacement.Decision);
        Assert.Equal(3, replacement.History.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DecideAsync(owner.UserId,
            queued.Id, 1, CampaignVideoReviewDecision.Approved, null));
        await Assert.ThrowsAsync<ArgumentException>(() => service.DecideAsync(owner.UserId,
            queued.Id, 2, CampaignVideoReviewDecision.ChangesRequested, " "));
        await service.DecideAsync(owner.UserId, queued.Id, 2, CampaignVideoReviewDecision.ChangesRequested, "Lower the music.");
        var changed = Assert.Single(await service.ListAsync(owner.UserId));
        Assert.Equal(CampaignVideoReviewDecision.ChangesRequested, changed.Decision);
        Assert.Equal(4, changed.History.Count);

        db.CampaignVideoReviews.Remove(await db.CampaignVideoReviews.FirstAsync(review => review.CampaignDeliverableId == video.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    private async Task GrantReviewAdminAsync(string userId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        if (!await roles.RoleExistsAsync(PlatformAdministration.Role))
        {
            var created = await roles.CreateAsync(new IdentityRole(PlatformAdministration.Role));
            Assert.True(created.Succeeded);
        }
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByIdAsync(userId);
        Assert.NotNull(user);
        Assert.True((await users.AddToRoleAsync(user, PlatformAdministration.Role)).Succeeded);
    }
}
