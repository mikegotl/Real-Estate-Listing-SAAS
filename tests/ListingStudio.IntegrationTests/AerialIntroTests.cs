using ListingStudio.Application.Audio;
using ListingStudio.Application.Authentication;
using ListingStudio.Application.Properties;
using ListingStudio.Domain.Billing;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Notifications;
using ListingStudio.Domain.Properties;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed class AerialIntroTests(PostgreSqlWebApplicationFixture fixture)
    : IClassFixture<PostgreSqlWebApplicationFixture>
{
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Z+VAAAAAASUVORK5CYII=");

    [Fact]
    public async Task DroneIntroNeedsAcknowledgementIsGeneratedReviewedAndKeptToItsOrganization()
    {
        var owner = await CreateOwnerAndPropertyAsync("aerial-owner");
        var outsider = await CreateOwnerAndPropertyAsync("aerial-outsider");
        await UploadAsync(owner, WalkthroughStoryboard.EntryFoyer);
        var front = await UploadAsync(owner, WalkthroughStoryboard.ExteriorFront);

        AerialIntroItem requested;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IAerialIntroService>();
            Assert.Null(await service.GetAsync(owner.UserId, owner.PropertyId));
            var unacknowledged = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.RequestAsync(owner.UserId, owner.PropertyId, AerialIntroClip.LowHeightFeet, false));
            Assert.Contains("AI-generated", unacknowledged.Message, StringComparison.Ordinal);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.RequestAsync(owner.UserId, owner.PropertyId, 75, true));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.RequestAsync(outsider.UserId, owner.PropertyId, AerialIntroClip.LowHeightFeet, true));

            requested = await service.RequestAsync(owner.UserId, owner.PropertyId, AerialIntroClip.LowHeightFeet, true);
            Assert.Equal((front, AerialIntroClipStatus.Pending, true), (requested.PropertyMediaId, requested.Status, requested.IsCurrent));
            Assert.False(await service.ApproveAsync(owner.UserId, owner.PropertyId));
            Assert.Null(await service.GetAsync(outsider.UserId, owner.PropertyId));
        }

        var submitted = await ProcessUntilAsync(requested.Id);
        Assert.Equal(AerialIntroClipStatus.Generating, submitted.Status);
        Assert.Equal(AerialIntroClip.LowHeightFeet, fixture.Factory.WalkthroughVideoProvider.SubmittedAerialHeights[requested.Id]);

        fixture.Factory.TimeProvider.Advance(TimeSpan.FromSeconds(11));
        fixture.Factory.WalkthroughVideoProvider.EnqueuePoll(requested.Id, WalkthroughVideoState.Completed);
        Assert.Equal(AerialIntroClipStatus.Completed, (await ProcessUntilAsync(requested.Id)).Status);
        var ready = Assert.Single((await NotificationInbox.ForAsync(fixture, owner.UserId)).Items);
        Assert.Equal((UserNotificationKind.AerialIntro, UserNotificationOutcome.Succeeded), (ready.Kind, ready.Outcome));
        Assert.Equal("Drone intro ready to review", ready.Title);
        Assert.Empty((await NotificationInbox.ForAsync(fixture, outsider.UserId)).Items);

        string firstAsset;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IAerialIntroService>();
            var item = (await service.GetAsync(owner.UserId, owner.PropertyId))!;
            Assert.True(item.IsReady);
            Assert.False(item.IsUsedInVideos);
            var clip = await service.OpenReadAsync(owner.UserId, requested.Id);
            Assert.NotNull(clip);
            await using (clip.Content)
            {
                using var buffer = new MemoryStream();
                await clip.Content.CopyToAsync(buffer);
                // The stored clip is the reversed, labeled edit, not the provider's raw pull-back.
                Assert.Equal(FakeAerialIntroVideoEditor.IntroBytes, buffer.ToArray());
                Assert.Equal("video/mp4", clip.ContentType);
            }

            Assert.Null(await service.OpenReadAsync(outsider.UserId, requested.Id));
            Assert.False(await service.ApproveAsync(outsider.UserId, owner.PropertyId));
            Assert.True(await service.ApproveAsync(owner.UserId, owner.PropertyId));
            Assert.True((await service.GetAsync(owner.UserId, owner.PropertyId))!.IsUsedInVideos);

            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var stored = await dbContext.AerialIntroClips.AsNoTracking().SingleAsync(candidate => candidate.Id == requested.Id);
            Assert.Equal(owner.UserId, stored.ApprovedByUserId);
            Assert.Equal(owner.UserId, stored.RequestedByUserId);
            firstAsset = stored.AssetPath!;

            // Asking again replaces the approved intro, and the new one needs its own approval.
            var replacement = await service.RequestAsync(owner.UserId, owner.PropertyId, AerialIntroClip.HighHeightFeet, true);
            Assert.NotEqual(requested.Id, replacement.Id);
            Assert.False(replacement.IsApproved);
            Assert.Equal(AerialIntroClip.HighHeightFeet, replacement.HeightFeet);
            Assert.Null(await scope.ServiceProvider.GetRequiredService<ICampaignAssetStorage>().OpenReadAsync(firstAsset));

            Assert.False(await service.RemoveAsync(outsider.UserId, owner.PropertyId));
            Assert.True(await service.RemoveAsync(owner.UserId, owner.PropertyId));
            Assert.Null(await service.GetAsync(owner.UserId, owner.PropertyId));
        }
    }

    [Fact]
    public async Task ReplacingTheFrontPhotoMakesTheIntroOutOfDate()
    {
        var owner = await CreateOwnerAndPropertyAsync("aerial-stale");
        var front = await UploadAsync(owner, WalkthroughStoryboard.ExteriorFront);
        AerialIntroItem requested;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            requested = await scope.ServiceProvider.GetRequiredService<IAerialIntroService>()
                .RequestAsync(owner.UserId, owner.PropertyId, AerialIntroClip.LowHeightFeet, true);
        }

        await ProcessUntilAsync(requested.Id);
        fixture.Factory.TimeProvider.Advance(TimeSpan.FromSeconds(11));
        fixture.Factory.WalkthroughVideoProvider.EnqueuePoll(requested.Id, WalkthroughVideoState.Completed);
        Assert.Equal(AerialIntroClipStatus.Completed, (await ProcessUntilAsync(requested.Id)).Status);

        // A new front photo moved ahead of the old one becomes the opening shot the drone would land on.
        var newFront = await UploadAsync(owner, WalkthroughStoryboard.ExteriorFront);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPropertyMediaService>()
                .ReorderAsync(owner.UserId, owner.PropertyId, [newFront, front]);
            var service = scope.ServiceProvider.GetRequiredService<IAerialIntroService>();
            var item = (await service.GetAsync(owner.UserId, owner.PropertyId))!;
            Assert.True(item.IsReady);
            Assert.False(item.IsCurrent);
            Assert.False(item.IsUsedInVideos);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApproveAsync(owner.UserId, owner.PropertyId));

            var remade = await service.RequestAsync(owner.UserId, owner.PropertyId, AerialIntroClip.LowHeightFeet, true);
            Assert.Equal(newFront, remade.PropertyMediaId);
        }
    }

    [Fact]
    public async Task IntroNeedsAnExteriorFrontPhotoAndReportsWhenItFailsForGood()
    {
        var owner = await CreateOwnerAndPropertyAsync("aerial-failure");
        await UploadAsync(owner, WalkthroughStoryboard.EntryFoyer);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var noFront = await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider
                .GetRequiredService<IAerialIntroService>()
                .RequestAsync(owner.UserId, owner.PropertyId, AerialIntroClip.LowHeightFeet, true));
            Assert.Contains("Exterior front", noFront.Message, StringComparison.Ordinal);
        }

        await UploadAsync(owner, WalkthroughStoryboard.ExteriorFront);
        AerialIntroItem requested;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            requested = await scope.ServiceProvider.GetRequiredService<IAerialIntroService>()
                .RequestAsync(owner.UserId, owner.PropertyId, AerialIntroClip.LowHeightFeet, true);
        }

        for (var attempt = 1; attempt <= AerialIntroClip.MaximumAttempts; attempt++)
        {
            fixture.Factory.WalkthroughVideoProvider.EnqueueSubmissionFailure(new HttpRequestException("provider unavailable"));
            var failed = await ProcessUntilAsync(requested.Id);
            Assert.Equal((AerialIntroClipStatus.Failed, attempt), (failed.Status, failed.AttemptNumber));
            Assert.Equal(
                attempt == AerialIntroClip.MaximumAttempts ? 1 : 0,
                (await NotificationInbox.ForAsync(fixture, owner.UserId)).Items.Count);
            fixture.Factory.TimeProvider.Advance(TimeSpan.FromMinutes(5));
        }

        var gaveUp = Assert.Single((await NotificationInbox.ForAsync(fixture, owner.UserId)).Items);
        Assert.Equal((UserNotificationKind.AerialIntro, UserNotificationOutcome.Failed), (gaveUp.Kind, gaveUp.Outcome));
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var item = (await scope.ServiceProvider.GetRequiredService<IAerialIntroService>()
                .GetAsync(owner.UserId, owner.PropertyId))!;
            Assert.Equal(AerialIntroClipStatus.Failed, item.Status);
            Assert.False(item.IsInProgress);
            Assert.Equal("The video provider rejected or could not complete the request.", item.Error);
        }
    }

    private async Task<AerialIntroRunResult> ProcessUntilAsync(Guid clipId)
    {
        for (var index = 0; index < 20; index++)
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<IAerialIntroProcessor>().ProcessNextAsync();
            if (result?.ClipId == clipId)
            {
                return result;
            }

            if (result is null)
            {
                break;
            }
        }

        throw new Xunit.Sdk.XunitException($"Drone intro {clipId} was not processed.");
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
        // The drone intro comes with Professional and Enterprise.
        await BillingPlanTestData.SubscribeAsync(
            fixture.Factory.Services, registration.UserId!, SubscriptionPlan.Professional, fixture.Factory.TimeProvider.GetUtcNow());
        return new OwnerProperty(registration.UserId!, propertyId);
    }

    private sealed record OwnerProperty(string UserId, Guid PropertyId);
}
