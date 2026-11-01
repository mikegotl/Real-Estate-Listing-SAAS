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
    public async Task FormatRecommendationReadsOnlyTheWorkspacesStoryboardAndConsentedPresenter()
    {
        var owner = await CreateOwnerAndPropertyAsync("recommend-owner");
        var outsider = await CreateOwnerAndPropertyAsync("recommend-outsider");
        var now = fixture.Factory.TimeProvider.GetUtcNow();
        Guid ownPortrait;
        Guid outsiderPortrait;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (PropertyMediaCategory Category, int Hero, int Quality)[] photos =
            [
                (PropertyMediaCategory.FrontExterior, 66, 78),
                (PropertyMediaCategory.Kitchen, 90, 86),
                (PropertyMediaCategory.LivingRoom, 74, 80),
                (PropertyMediaCategory.PrimaryBedroom, 52, 72),
                (PropertyMediaCategory.Backyard, 63, 70),
            ];
            for (var index = 0; index < photos.Length; index++)
            {
                db.PropertyMedia.Add(AnalyzedMedia(owner, index, photos[index].Category, photos[index].Hero,
                    photos[index].Quality, now, stop: $"Stop {index + 1}"));
            }

            // Not on the storyboard, so the rules must ignore them however good they look.
            var extra = AnalyzedMedia(owner, 10, PropertyMediaCategory.Pool, 99, 99, now, stop: "Pool");
            extra.MoveToGallery();
            db.PropertyMedia.Add(extra);
            db.PropertyMedia.Add(AnalyzedMedia(owner, 11, PropertyMediaCategory.Pool, 99, 99, now, stop: null));

            // Another workspace's photo, on its own property, never counts here.
            db.PropertyMedia.Add(AnalyzedMedia(outsider, 12, PropertyMediaCategory.Pool, 99, 99, now, stop: "Stop 1"));

            var portrait = AgentPortrait.Create(owner.OrganizationId, owner.UserId, "portraits/own.jpg", "own.jpg",
                "image/jpeg", 1_000, 800, 800, now);
            var foreign = AgentPortrait.Create(outsider.OrganizationId, outsider.UserId, "portraits/other.jpg",
                "other.jpg", "image/jpeg", 1_000, 800, 800, now);
            db.AgentPortraits.AddRange(portrait, foreign);
            await db.SaveChangesAsync();
            ownPortrait = portrait.Id;
            outsiderPortrait = foreign.Id;
        }

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IVerticalFormatRecommendationService>();
            var result = await service.RecommendAsync(owner.UserId, owner.PropertyId, VerticalRecommendationContext.None);

            Assert.NotNull(result);
            Assert.Equal(5, result.Inputs.StoryboardPhotos);
            Assert.Equal(5, result.Inputs.UsablePhotos);
            Assert.True(result.Inputs.HasPrice && result.Inputs.HasCoreFacts && result.Inputs.HasCityAndState);
            Assert.Equal(
                [VerticalCreativeFormat.WowFeature, VerticalCreativeFormat.PriceFirst],
                result.Recommended.Select(item => item.Format).Order());
            Assert.False(result.For(VerticalCreativeFormat.HostedPov).IsEligible);
            Assert.False(result.For(VerticalCreativeFormat.StoryReveal).IsEligible);
            Assert.False(result.For(VerticalCreativeFormat.ExclusiveAccess).IsEligible);
            Assert.DoesNotContain(result.For(VerticalCreativeFormat.WowFeature).Reasons,
                reason => reason.Contains("pool", StringComparison.OrdinalIgnoreCase));

            // Consent counts only for this workspace's own portrait, and only when the request confirmed it.
            var unconsented = await service.RecommendAsync(owner.UserId, owner.PropertyId,
                new VerticalRecommendationContext(ownPortrait, false));
            Assert.True(unconsented!.Inputs.HasPresenterPortrait);
            Assert.False(unconsented.For(VerticalCreativeFormat.HostedPov).IsEligible);
            var consented = await service.RecommendAsync(owner.UserId, owner.PropertyId,
                new VerticalRecommendationContext(ownPortrait, true));
            Assert.True(consented!.For(VerticalCreativeFormat.HostedPov).IsEligible);
            var foreignPortrait = await service.RecommendAsync(owner.UserId, owner.PropertyId,
                new VerticalRecommendationContext(outsiderPortrait, true));
            Assert.False(foreignPortrait!.Inputs.HasPresenterPortrait);
            Assert.False(foreignPortrait.For(VerticalCreativeFormat.HostedPov).IsEligible);

            // Workspace scoping: another workspace's user cannot score this property.
            Assert.Null(await service.RecommendAsync(outsider.UserId, owner.PropertyId, VerticalRecommendationContext.None));
            Assert.Null(await service.RecommendForOrganizationAsync(
                outsider.OrganizationId, owner.PropertyId, VerticalRecommendationContext.None));
            var outsiderOwn = await service.RecommendAsync(outsider.UserId, outsider.PropertyId, VerticalRecommendationContext.None);
            Assert.Equal(1, outsiderOwn!.Inputs.StoryboardPhotos);
        }
    }

    [Fact]
    public async Task LibraryKeepsTheRecommendationSnapshotItWasCreatedWith()
    {
        var owner = await CreateOwnerAndPropertyAsync("recommend-snapshot");
        var now = fixture.Factory.TimeProvider.GetUtcNow();
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            for (var index = 0; index < 4; index++)
            {
                db.PropertyMedia.Add(AnalyzedMedia(owner, index, PropertyMediaCategory.Kitchen, 85, 85, now, $"Stop {index + 1}"));
            }

            await db.SaveChangesAsync();
        }

        var queued = await EnqueueAsync(owner, CampaignOutputSelection.TikTok);
        Guid libraryId;
        string snapshot;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<ICampaignGenerationService>()
                .CancelAsync(owner.UserId, queued.Id));
            var recommendation = await scope.ServiceProvider.GetRequiredService<IVerticalFormatRecommendationService>()
                .RecommendAsync(owner.UserId, owner.PropertyId, VerticalRecommendationContext.None);
            snapshot = recommendation!.ToSnapshotJson();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var library = CampaignVideoLibrary.Create(owner.OrganizationId, owner.PropertyId, queued.Id,
                VerticalVideoSelection.RecommendedThree, recommendation.RuleVersion, snapshot, now);
            db.CampaignVideoLibraries.Add(library);
            await db.SaveChangesAsync();
            libraryId = library.Id;
        }

        // The listing changes afterwards: new rules or new facts must not rewrite what the campaign was shown.
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.PropertyMedia.Where(media => media.PropertyId == owner.PropertyId)
                .ExecuteDeleteAsync();
            var stored = await db.CampaignVideoLibraries.AsNoTracking()
                .SingleAsync(library => library.Id == libraryId && library.OrganizationId == owner.OrganizationId);
            var restored = VerticalFormatRecommendationSet.FromSnapshotJson(stored.RecommendationSnapshotJson);

            Assert.Equal(VerticalFormatRules.Version, stored.RuleVersion);
            Assert.NotNull(restored);
            Assert.Equal(snapshot, restored.ToSnapshotJson());
            Assert.Contains(restored.Recommended, item => item.Format == VerticalCreativeFormat.WowFeature);
            Assert.DoesNotContain("A comfortable home", stored.RecommendationSnapshotJson!, StringComparison.Ordinal);
            Assert.DoesNotContain("Main Street", stored.RecommendationSnapshotJson!, StringComparison.Ordinal);

            var now2 = await scope.ServiceProvider.GetRequiredService<IVerticalFormatRecommendationService>()
                .RecommendAsync(owner.UserId, owner.PropertyId, VerticalRecommendationContext.None);
            Assert.Empty(now2!.Recommended);
        }
    }

    private static PropertyMedia AnalyzedMedia(
        OwnerProperty owner,
        int index,
        PropertyMediaCategory category,
        int hero,
        int quality,
        DateTimeOffset now,
        string? stop)
    {
        var media = PropertyMedia.Create(owner.OrganizationId, owner.PropertyId,
            $"tests/recommend/{Guid.NewGuid():N}.jpg", $"photo-{index}.jpg", "image/jpeg", 1_000, 4032, 3024, index);
        media.SetWalkthroughStop(stop);
        media.BeginAnalysis(now);
        media.CompleteAnalysis(new PropertyMediaAnalysis(category, category.ToString(), quality, hero,
            category is PropertyMediaCategory.FrontExterior or PropertyMediaCategory.Backyard or PropertyMediaCategory.Pool,
            category is not (PropertyMediaCategory.FrontExterior or PropertyMediaCategory.Backyard or PropertyMediaCategory.Pool),
            false, [], "Test photo.", index), now);
        return media;
    }
}
