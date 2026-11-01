using ListingStudio.Application.Campaigns;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ListingStudio.IntegrationTests;

/// <summary>Prompt 7: the agent's goal and own pick of vertical videos, end to end.</summary>
public sealed partial class CampaignGenerationTests
{
    [Fact]
    public async Task AgentPickedVideosAreTheOnlyOnesMadeAndTheGoalIsKept()
    {
        var (admin, owner) = await CreateLibraryListingAsync("library-agent-pick");
        try
        {
            await SetLibrarySwitchAsync(admin, true, true);
            await using (var check = fixture.Factory.Services.CreateAsyncScope())
            {
                Assert.True(await check.ServiceProvider.GetRequiredService<ICampaignGenerationService>()
                    .IsVerticalLibraryEnabledAsync(owner.UserId));
            }

            QueueLibraryProviders();
            var request = new VerticalLibraryRequest(
                VerticalVideoSelection.AgentChosen, VerticalCampaignGoal.BuyerInterest, [VerticalCreativeFormat.PriceFirst]);
            var queued = await EnqueueVerticalRequestAsync(owner, request);

            Assert.NotNull(queued.VerticalRequest);
            Assert.Equal(VerticalVideoSelection.AgentChosen, queued.VerticalRequest.Selection);
            Assert.Equal(VerticalCampaignGoal.BuyerInterest, queued.VerticalRequest.Goal);
            Assert.Equal([VerticalCreativeFormat.PriceFirst], queued.VerticalRequest.Formats);
            // The same pick again finds the same campaign; a different goal is a different request.
            Assert.Equal(queued.Id, (await EnqueueVerticalRequestAsync(owner, request)).Id);

            await ProcessUntilAsync(owner, queued.Id, CampaignGenerationStatus.Completed);
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var library = await db.CampaignVideoLibraries.AsNoTracking().Include(item => item.Variants)
                .SingleAsync(item => item.CampaignGenerationJobId == queued.Id);
            Assert.Equal(VerticalVideoSelection.AgentChosen, library.Selection);
            var variant = Assert.Single(library.Variants);
            Assert.Equal(VerticalCreativeFormat.PriceFirst, variant.Format);
            Assert.Equal(CampaignVideoVariantStatus.Rendered, variant.Status);
            var job = await db.CampaignGenerationJobs.AsNoTracking().SingleAsync(item => item.Id == queued.Id);
            Assert.Equal(VerticalCampaignGoal.BuyerInterest, job.VerticalGoal);
            Assert.Equal("PriceFirst", job.VerticalFormatsValue);
        }
        finally
        {
            await SetLibrarySwitchAsync(admin, false, false);
        }
    }

    [Fact]
    public async Task PickingAVideoTheListingCannotSupportIsRejectedUnlessTheLibraryIsOff()
    {
        var (admin, owner) = await CreateLibraryListingAsync("library-agent-ineligible");
        try
        {
            await SetLibrarySwitchAsync(admin, true, true);
            var request = new VerticalLibraryRequest(
                VerticalVideoSelection.AgentChosen, VerticalCampaignGoal.MaximizeReach, [VerticalCreativeFormat.StoryReveal]);

            var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() => EnqueueVerticalRequestAsync(owner, request));
            Assert.Contains("Story reveal can't be made for this listing yet", rejected.Message, StringComparison.Ordinal);
            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                Assert.False(await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().CampaignGenerationJobs
                    .AnyAsync(item => item.PropertyId == owner.PropertyId));
            }

            // Switched off, the same request is the legacy campaign: nothing about the library is kept or checked.
            await SetLibrarySwitchAsync(admin, false, false);
            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                var service = scope.ServiceProvider.GetRequiredService<ICampaignGenerationService>();
                Assert.False(await service.IsVerticalLibraryEnabledAsync(owner.UserId));
                var legacy = (await service.EnqueueAsync(owner.UserId, owner.PropertyId, CampaignOutputSelection.TikTok,
                    CampaignCreativeOptions.SocialDefault, CampaignNarrationPreferences.Default, request))!;
                Assert.Null(legacy.VerticalRequest);
                var job = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().CampaignGenerationJobs
                    .AsNoTracking().SingleAsync(item => item.Id == legacy.Id);
                Assert.Null(job.VerticalSelection);
                Assert.Null(job.VerticalGoal);
                Assert.Null(job.VerticalFormatsValue);
                await service.CancelAsync(owner.UserId, legacy.Id);
            }
        }
        finally
        {
            await SetLibrarySwitchAsync(admin, false, false);
        }
    }

    private async Task<CampaignGenerationResult> EnqueueVerticalRequestAsync(OwnerProperty owner, VerticalLibraryRequest request)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<ICampaignGenerationService>().EnqueueAsync(
            owner.UserId, owner.PropertyId, CampaignOutputSelection.TikTok, CampaignCreativeOptions.SocialDefault,
            CampaignNarrationPreferences.Default, request))!;
    }
}
