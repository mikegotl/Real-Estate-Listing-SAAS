using ListingStudio.Application.Audio;
using ListingStudio.Application.Authentication;
using ListingStudio.Application.Properties;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Properties;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed class WalkthroughTransitionTests(PostgreSqlWebApplicationFixture fixture)
    : IClassFixture<PostgreSqlWebApplicationFixture>
{
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Z+VAAAAAASUVORK5CYII=");

    [Fact]
    public async Task QueuesEachConsecutiveShotPairGeneratesItAndServesItOnlyToItsOrganization()
    {
        var owner = await CreateOwnerAndPropertyAsync("walkthrough-owner");
        var outsider = await CreateOwnerAndPropertyAsync("walkthrough-outsider");
        // Uploaded out of order: the storyboard puts the front first, then the foyer, then the kitchen.
        var kitchen = await UploadAsync(owner, "Kitchen");
        var front = await UploadAsync(owner, WalkthroughStoryboard.ExteriorFront);
        var foyer = await UploadAsync(owner, WalkthroughStoryboard.EntryFoyer);

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IWalkthroughTransitionService>();
            var pairs = await service.ListAsync(owner.UserId, owner.PropertyId);
            Assert.Equal([(front, foyer), (foyer, kitchen)], pairs.Select(item => (item.FromPropertyMediaId, item.ToPropertyMediaId)));
            Assert.All(pairs, item => Assert.Null(item.Status));
            Assert.Empty(await service.ListAsync(outsider.UserId, owner.PropertyId));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.QueueAsync(outsider.UserId, owner.PropertyId));

            Assert.Equal(2, await service.QueueAsync(owner.UserId, owner.PropertyId));
            Assert.Equal(0, await service.QueueAsync(owner.UserId, owner.PropertyId));
            Assert.All(
                await service.ListAsync(owner.UserId, owner.PropertyId),
                item => Assert.Equal(WalkthroughTransitionStatus.Pending, item.Status));
        }

        var first = await TransitionIdAsync(owner, front, foyer);
        var second = await TransitionIdAsync(owner, foyer, kitchen);
        var submitted = await ProcessAllAsync(first, second);
        Assert.All(submitted.Values, result => Assert.Equal(WalkthroughTransitionStatus.Generating, result.Status));

        // The first clip is still generating on its first check and is collected on the next one.
        fixture.Factory.TimeProvider.Advance(TimeSpan.FromSeconds(11));
        fixture.Factory.WalkthroughVideoProvider.EnqueuePoll(second, WalkthroughVideoState.Completed);
        var checkedOnce = await ProcessAllAsync(first, second);
        Assert.Equal(WalkthroughTransitionStatus.Generating, checkedOnce[first].Status);
        Assert.Equal(WalkthroughTransitionStatus.Completed, checkedOnce[second].Status);
        fixture.Factory.TimeProvider.Advance(TimeSpan.FromSeconds(11));
        fixture.Factory.WalkthroughVideoProvider.EnqueuePoll(first, WalkthroughVideoState.Completed);
        var checkedTwice = await ProcessAllAsync(first, second);
        Assert.Equal(WalkthroughTransitionStatus.Completed, Assert.Single(checkedTwice.Values).Status);

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IWalkthroughTransitionService>();
            var items = await service.ListAsync(owner.UserId, owner.PropertyId);
            Assert.All(items, item => Assert.True(item.IsReady));
            var clip = await service.OpenReadAsync(owner.UserId, first);
            Assert.NotNull(clip);
            await using (clip.Content)
            {
                using var buffer = new MemoryStream();
                await clip.Content.CopyToAsync(buffer);
                Assert.Equal(FakeWalkthroughVideoProvider.ClipBytes, buffer.ToArray());
                Assert.Equal("video/mp4", clip.ContentType);
            }

            Assert.Null(await service.OpenReadAsync(outsider.UserId, first));
        }

        string? foyerClipPath;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            foyerClipPath = (await dbContext.WalkthroughTransitions.AsNoTracking().SingleAsync(item => item.Id == first)).AssetPath;
            Assert.True(await scope.ServiceProvider.GetRequiredService<IPropertyMediaService>()
                .DeleteAsync(owner.UserId, owner.PropertyId, foyer));
        }

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.False(await dbContext.WalkthroughTransitions.AnyAsync(item => item.Id == first || item.Id == second));
            Assert.Null(await scope.ServiceProvider.GetRequiredService<ICampaignAssetStorage>().OpenReadAsync(foyerClipPath!));
            // Removing the foyer bridges the front straight to the kitchen.
            var bridged = Assert.Single(await scope.ServiceProvider.GetRequiredService<IWalkthroughTransitionService>()
                .ListAsync(owner.UserId, owner.PropertyId));
            Assert.Equal((front, kitchen), (bridged.FromPropertyMediaId, bridged.ToPropertyMediaId));
            Assert.Null(bridged.Status);
        }
    }

    [Fact]
    public async Task FailedGenerationRetriesThenCanBeRequestedAgain()
    {
        var owner = await CreateOwnerAndPropertyAsync("walkthrough-retry");
        var front = await UploadAsync(owner, WalkthroughStoryboard.ExteriorFront);
        var foyer = await UploadAsync(owner, WalkthroughStoryboard.EntryFoyer);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<IWalkthroughTransitionService>()
                .QueueAsync(owner.UserId, owner.PropertyId));
        }

        var transitionId = await TransitionIdAsync(owner, front, foyer);
        for (var attempt = 1; attempt <= WalkthroughTransition.MaximumAttempts; attempt++)
        {
            fixture.Factory.WalkthroughVideoProvider.EnqueueSubmissionFailure(
                new HttpRequestException("provider unavailable"));
            var failed = await ProcessUntilAsync(transitionId);
            Assert.Equal(WalkthroughTransitionStatus.Failed, failed.Status);
            Assert.Equal(attempt, failed.AttemptNumber);
            Assert.Equal("The walk-through provider rejected or could not complete the request.", failed.Error);
            fixture.Factory.TimeProvider.Advance(TimeSpan.FromMinutes(5));
        }

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IWalkthroughTransitionService>();
            var item = Assert.Single(await service.ListAsync(owner.UserId, owner.PropertyId));
            Assert.Equal(WalkthroughTransitionStatus.Failed, item.Status);
            Assert.NotNull(item.Error);
            Assert.Equal(1, await service.QueueAsync(owner.UserId, owner.PropertyId));
        }

        Assert.Equal(WalkthroughTransitionStatus.Generating, (await ProcessUntilAsync(transitionId)).Status);
        fixture.Factory.TimeProvider.Advance(TimeSpan.FromSeconds(11));
        fixture.Factory.WalkthroughVideoProvider.EnqueuePoll(transitionId, WalkthroughVideoState.Failed);
        var rejected = await ProcessUntilAsync(transitionId);
        Assert.Equal(WalkthroughTransitionStatus.Failed, rejected.Status);
        Assert.Equal("The fake provider rejected the photos.", rejected.Error);
    }

    [Fact]
    public async Task ClipsFollowTheWalkthroughMapDirectionAndAreRemadeWhenItChanges()
    {
        var owner = await CreateOwnerAndPropertyAsync("walkthrough-direction");
        var front = await UploadAsync(owner, WalkthroughStoryboard.ExteriorFront);
        var foyer = await UploadAsync(owner, WalkthroughStoryboard.EntryFoyer);
        var kitchen = await UploadAsync(owner, "Kitchen");
        var secondKitchen = await UploadAsync(owner, "Kitchen");
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<IPropertyMediaService>()
                .SetStopDirectionAsync(owner.UserId, owner.PropertyId, WalkthroughStoryboard.EntryFoyer, WalkthroughDirection.Ahead));
            Assert.Equal(3, await scope.ServiceProvider.GetRequiredService<IWalkthroughTransitionService>()
                .QueueAsync(owner.UserId, owner.PropertyId));
        }

        var intoFoyer = await TransitionIdAsync(owner, front, foyer);
        var intoKitchen = await TransitionIdAsync(owner, foyer, kitchen);
        var withinKitchen = await TransitionIdAsync(owner, kitchen, secondKitchen);
        Assert.Equal(3, (await ProcessAllAsync(intoFoyer, intoKitchen, withinKitchen)).Count);
        var submitted = fixture.Factory.WalkthroughVideoProvider.SubmittedDirections;
        Assert.Equal(WalkthroughDirection.Ahead, submitted[intoFoyer]);
        // No direction is set for the kitchen yet, and two angles of one stop never get a turn.
        Assert.Null(submitted[intoKitchen]);
        Assert.Null(submitted[withinKitchen]);
        fixture.Factory.TimeProvider.Advance(TimeSpan.FromSeconds(11));
        fixture.Factory.WalkthroughVideoProvider.EnqueuePoll(intoKitchen, WalkthroughVideoState.Completed);
        Assert.Equal(WalkthroughTransitionStatus.Completed, (await ProcessUntilAsync(intoKitchen)).Status);

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<IPropertyMediaService>()
                .SetStopDirectionAsync(owner.UserId, owner.PropertyId, "kitchen", WalkthroughDirection.Left));
            var service = scope.ServiceProvider.GetRequiredService<IWalkthroughTransitionService>();
            var stale = (await service.ListAsync(owner.UserId, owner.PropertyId))
                .Single(item => item.FromPropertyMediaId == foyer);
            // The clip made without a direction keeps playing, flagged so a clip that turns left can replace it.
            Assert.True(stale.IsReady);
            Assert.False(stale.IsCurrent);
            Assert.All(
                (await service.ListAsync(owner.UserId, owner.PropertyId)).Where(item => item.FromPropertyMediaId != foyer),
                item => Assert.True(item.IsCurrent));

            Assert.Equal(1, await service.QueueAsync(owner.UserId, owner.PropertyId));
            Assert.Equal(0, await service.QueueAsync(owner.UserId, owner.PropertyId));
            var remade = (await service.ListAsync(owner.UserId, owner.PropertyId))
                .Single(item => item.FromPropertyMediaId == foyer);
            Assert.Equal(WalkthroughTransitionStatus.Pending, remade.Status);
            Assert.True(remade.IsCurrent);
            Assert.NotEqual(intoKitchen, remade.TransitionId);
        }

        var turningLeft = await TransitionIdAsync(owner, foyer, kitchen);
        await ProcessUntilAsync(turningLeft);
        Assert.Equal(WalkthroughDirection.Left, submitted[turningLeft]);
    }

    private async Task<Dictionary<Guid, WalkthroughTransitionRunResult>> ProcessAllAsync(params Guid[] transitionIds)
    {
        var results = new Dictionary<Guid, WalkthroughTransitionRunResult>();
        for (var index = 0; index < 20; index++)
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<IWalkthroughTransitionProcessor>()
                .ProcessNextAsync();
            if (result is null)
            {
                return results;
            }

            if (transitionIds.Contains(result.TransitionId))
            {
                results[result.TransitionId] = result;
            }
        }

        throw new Xunit.Sdk.XunitException("Walk-through processing did not settle.");
    }

    private async Task<WalkthroughTransitionRunResult> ProcessUntilAsync(Guid transitionId)
    {
        for (var index = 0; index < 20; index++)
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<IWalkthroughTransitionProcessor>()
                .ProcessNextAsync();
            if (result?.TransitionId == transitionId)
            {
                return result;
            }

            if (result is null)
            {
                break;
            }
        }

        throw new Xunit.Sdk.XunitException($"Walk-through transition {transitionId} was not processed.");
    }

    private async Task<Guid> TransitionIdAsync(OwnerProperty owner, Guid from, Guid to)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var items = await scope.ServiceProvider.GetRequiredService<IWalkthroughTransitionService>()
            .ListAsync(owner.UserId, owner.PropertyId);
        return items.Single(item => item.FromPropertyMediaId == from && item.ToPropertyMediaId == to).TransitionId!.Value;
    }

    private async Task<Guid> UploadAsync(OwnerProperty owner, string stop)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        await using var content = new MemoryStream(OnePixelPng);
        return await scope.ServiceProvider.GetRequiredService<IPropertyMediaService>().UploadAsync(
            owner.UserId,
            owner.PropertyId,
            new PropertyMediaUpload($"{stop}.png", "image/png", content.Length, content, stop));
    }

    private async Task<OwnerProperty> CreateOwnerAndPropertyAsync(string prefix)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var registration = await scope.ServiceProvider.GetRequiredService<IAccountRegistrationService>()
            .RegisterAsync(new RegisterAccountCommand(
                $"{prefix}-{Guid.NewGuid():N}@example.com",
                "Password123",
                $"{prefix} Realty {Guid.NewGuid():N}"));
        Assert.True(registration.Succeeded, string.Join(", ", registration.Errors));
        var propertyId = await scope.ServiceProvider.GetRequiredService<IPropertyService>().CreateAsync(
            registration.UserId!,
            new PropertyInput(
                "123 Main Street", null, "Raleigh", "NC", "27601", 450_000m, 3, 2.5m, 2_100, 0.25m, 1998,
                PropertyType.SingleFamily, "A comfortable home.", ListingStatus.Draft));
        return new OwnerProperty(registration.UserId!, propertyId);
    }

    private sealed record OwnerProperty(string UserId, Guid PropertyId);
}
