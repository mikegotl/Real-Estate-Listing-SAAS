using ListingStudio.Application.Authentication;
using ListingStudio.Application.Properties;
using ListingStudio.Domain.Notifications;
using ListingStudio.Domain.Properties;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed class PropertyPhotoFixTests(PostgreSqlWebApplicationFixture fixture)
    : IClassFixture<PostgreSqlWebApplicationFixture>
{
    [Fact]
    public async Task FixesReplaceThePhotoKeepTheOriginalAndRevertPutsItBack()
    {
        var owner = await CreateOwnerAndPropertyAsync("photo-fix-owner");
        var kitchen = await UploadAsync(owner, "Kitchen");

        await RequestAsync(owner, kitchen, PhotoFix.BrightnessAndColor);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var running = Assert.Single(await scope.ServiceProvider.GetRequiredService<IPropertyPhotoFixService>()
                .ListAsync(owner.UserId, owner.PropertyId));
            Assert.Equal((kitchen, PhotoFix.BrightnessAndColor, true), (running.PropertyMediaId, running.Kind, running.IsInProgress));
        }

        var brightened = await ProcessNextAsync();
        Assert.True(brightened.Succeeded, brightened.Error);
        var fixedNote = Assert.Single((await NotificationInbox.ForAsync(fixture, owner.UserId)).Items);
        Assert.Equal((UserNotificationKind.PhotoFixes, UserNotificationOutcome.Succeeded), (fixedNote.Kind, fixedNote.Outcome));
        Assert.Equal("The fixed photo for 123 Main Street is in place on the Media tab.", fixedNote.Message);
        Assert.Equal($"/properties/{owner.PropertyId}?tab=media", fixedNote.Link);
        var afterBrightness = await GetMediaAsync(owner, kitchen);
        Assert.Equal(PhotoFix.BrightnessAndColor, afterBrightness.AppliedFixes);
        Assert.True(afterBrightness.HasOriginal);
        Assert.NotNull(afterBrightness.EditedAtUtc);
        Assert.Equal("image/jpeg", afterBrightness.MimeType);
        Assert.Equal((FakePhotoImageCodec.SourceWidth, FakePhotoImageCodec.SourceHeight), (afterBrightness.Width, afterBrightness.Height));
        Assert.Equal("Kitchen", afterBrightness.WalkthroughStop);
        // New content is analyzed again, so the quality badge describes the fixed photo.
        Assert.Equal(PropertyMediaAnalysisStatus.Pending, afterBrightness.AnalysisStatus);
        Assert.Equal(FakePhotoImageCodec.Jpeg, await ReadPhotoAsync(owner, kitchen));
        Assert.Equal(FakePhotoImageCodec.Png, await ReadOriginalAsync(owner, kitchen));
        var brightenedPath = await BlobPathAsync(kitchen);

        // A second fix builds on the first; the first fixed file is no longer needed.
        var requestsBefore = fixture.Factory.PhotoClutterRemover.Requests.Count;
        await RequestAsync(owner, kitchen, PhotoFix.RemoveClutter);
        var decluttered = await ProcessNextAsync();
        Assert.True(decluttered.Succeeded, decluttered.Error);
        // The second fix gets its own note; the first is not counted again.
        Assert.All(
            (await NotificationInbox.ForAsync(fixture, owner.UserId)).Items,
            note => Assert.Equal("The fixed photo for 123 Main Street is in place on the Media tab.", note.Message));
        Assert.Equal(2, (await NotificationInbox.ForAsync(fixture, owner.UserId)).Items.Count);
        var sent = fixture.Factory.PhotoClutterRemover.Requests.Skip(requestsBefore).Single();
        Assert.Equal((1_536, 1_024), (sent.Width, sent.Height));
        var afterClutter = await GetMediaAsync(owner, kitchen);
        Assert.Equal(PhotoFix.BrightnessAndColor | PhotoFix.RemoveClutter, afterClutter.AppliedFixes);
        // The 4:3 photo was padded onto the 3:2 canvas and cut back out at 4:3.
        Assert.Equal((1_365, 1_024), (afterClutter.Width, afterClutter.Height));
        Assert.Null(await OpenStoredAsync(brightenedPath));
        Assert.Equal(FakePhotoImageCodec.Png, await ReadOriginalAsync(owner, kitchen));

        var fixedPath = await BlobPathAsync(kitchen);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<IPropertyPhotoFixService>()
                .RevertAsync(owner.UserId, owner.PropertyId, kitchen));
        }

        var reverted = await GetMediaAsync(owner, kitchen);
        Assert.Equal(PhotoFix.None, reverted.AppliedFixes);
        Assert.False(reverted.HasOriginal);
        Assert.Equal(("image/png", 1, 1), (reverted.MimeType, reverted.Width, reverted.Height));
        Assert.Equal(FakePhotoImageCodec.Png, await ReadPhotoAsync(owner, kitchen));
        Assert.Null(await OpenStoredAsync(fixedPath));
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IPropertyPhotoFixService>();
            Assert.Null(await service.OpenOriginalReadAsync(owner.UserId, kitchen));
            Assert.Empty(await service.ListAsync(owner.UserId, owner.PropertyId));
        }
    }

    [Fact]
    public async Task AnotherOrganizationCannotFixRevertOrSeeTheOriginal()
    {
        var owner = await CreateOwnerAndPropertyAsync("photo-fix-scope-owner");
        var outsider = await CreateOwnerAndPropertyAsync("photo-fix-scope-outsider");
        var photo = await UploadAsync(owner, WalkthroughStoryboard.ExteriorFront);
        await RequestAsync(owner, photo, PhotoFix.BrightnessAndColor);
        Assert.True((await ProcessNextAsync()).Succeeded);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IPropertyPhotoFixService>();
        Assert.False(await service.RequestAsync(outsider.UserId, owner.PropertyId, photo, PhotoFix.BrightnessAndColor));
        Assert.False(await service.RequestAsync(outsider.UserId, outsider.PropertyId, photo, PhotoFix.BrightnessAndColor));
        Assert.False(await service.RevertAsync(outsider.UserId, owner.PropertyId, photo));
        Assert.Null(await service.OpenOriginalReadAsync(outsider.UserId, photo));
        Assert.Empty(await service.ListAsync(outsider.UserId, owner.PropertyId));
        Assert.NotNull(await service.OpenOriginalReadAsync(owner.UserId, photo));
    }

    [Fact]
    public async Task ARunningFixBlocksAnotherFixAndRevertUntilItFinishes()
    {
        var owner = await CreateOwnerAndPropertyAsync("photo-fix-busy");
        var photo = await UploadAsync(owner, "Living room");
        await RequestAsync(owner, photo, PhotoFix.BrightnessAndColor);

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IPropertyPhotoFixService>();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.RequestAsync(owner.UserId, owner.PropertyId, photo, PhotoFix.RemoveClutter));
        }

        Assert.True((await ProcessNextAsync()).Succeeded);
        await RequestAsync(owner, photo, PhotoFix.BrightnessAndColor);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider
                .GetRequiredService<IPropertyPhotoFixService>()
                .RevertAsync(owner.UserId, owner.PropertyId, photo));
        }

        Assert.True((await ProcessNextAsync()).Succeeded);
    }

    [Fact]
    public async Task ARejectedEditFailsForGoodAndAConnectionFailureIsRetried()
    {
        var owner = await CreateOwnerAndPropertyAsync("photo-fix-failures");
        var photo = await UploadAsync(owner, "Primary bedroom");

        fixture.Factory.PhotoClutterRemover.EnqueueFailure(new PhotoFixRejectedException("OpenAI declined to edit this photo."));
        await RequestAsync(owner, photo, PhotoFix.RemoveClutter);
        var rejected = await ProcessNextAsync();
        Assert.False(rejected.Succeeded);
        Assert.False(rejected.WillRetry);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var failed = Assert.Single(await scope.ServiceProvider.GetRequiredService<IPropertyPhotoFixService>()
                .ListAsync(owner.UserId, owner.PropertyId));
            Assert.Equal(PropertyPhotoFixStatus.Failed, failed.Status);
            Assert.False(failed.IsInProgress);
            Assert.Equal("OpenAI declined to edit this photo.", failed.Error);
        }

        Assert.Equal(PhotoFix.None, (await GetMediaAsync(owner, photo)).AppliedFixes);
        var failedNote = Assert.Single((await NotificationInbox.ForAsync(fixture, owner.UserId)).Items);
        Assert.Equal((UserNotificationKind.PhotoFixes, UserNotificationOutcome.Failed), (failedNote.Kind, failedNote.Outcome));
        Assert.Equal("A photo for 123 Main Street could not be fixed. OpenAI declined to edit this photo.", failedNote.Message);

        fixture.Factory.PhotoClutterRemover.EnqueueFailure(new HttpRequestException("connection reset"));
        await RequestAsync(owner, photo, PhotoFix.RemoveClutter);
        var dropped = await ProcessNextAsync();
        Assert.False(dropped.Succeeded);
        Assert.True(dropped.WillRetry);
        Assert.Equal("The photo editing service could not be reached.", dropped.Error);
        Assert.Single((await NotificationInbox.ForAsync(fixture, owner.UserId)).Items);
        fixture.Factory.TimeProvider.Advance(TimeSpan.FromMinutes(2));
        var retried = await ProcessNextAsync();
        Assert.Equal((dropped.FixId, true, 2), (retried.FixId, retried.Succeeded, retried.AttemptNumber));
        Assert.Equal(PhotoFix.RemoveClutter, (await GetMediaAsync(owner, photo)).AppliedFixes);
        Assert.Contains(
            (await NotificationInbox.ForAsync(fixture, owner.UserId)).Items,
            note => note.Message == "The fixed photo for 123 Main Street is in place on the Media tab.");
    }

    [Fact]
    public async Task AFixMadeFromAPhotoThatChangedIsNotApplied()
    {
        var owner = await CreateOwnerAndPropertyAsync("photo-fix-stale");
        var photo = await UploadAsync(owner, "Dining room");
        await RequestAsync(owner, photo, PhotoFix.BrightnessAndColor);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            // As if the photo had been replaced after the fix was requested.
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""UPDATE "PropertyPhotoFixes" SET "SourceBlobPath" = 'organizations/elsewhere.png' WHERE "PropertyMediaId" = {photo}""");
        }

        var result = await ProcessNextAsync();
        Assert.False(result.Succeeded);
        Assert.False(result.WillRetry);
        Assert.Equal("The photo changed while it was being fixed, so the fix was not applied.", result.Error);
        var media = await GetMediaAsync(owner, photo);
        Assert.Equal(PhotoFix.None, media.AppliedFixes);
        Assert.Equal(FakePhotoImageCodec.Png, await ReadPhotoAsync(owner, photo));
    }

    [Fact]
    public async Task FixingOrRevertingAPhotoReplacesOnlyTheClipsMadeFromIt()
    {
        var owner = await CreateOwnerAndPropertyAsync("photo-fix-clips");
        var front = await UploadAsync(owner, WalkthroughStoryboard.ExteriorFront);
        var foyer = await UploadAsync(owner, WalkthroughStoryboard.EntryFoyer);
        var kitchen = await UploadAsync(owner, "Kitchen");
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<IWalkthroughTransitionService>()
                .QueueAsync(owner.UserId, owner.PropertyId));
        }

        var original = await ClipVersionsAsync(owner);
        await RequestAsync(owner, kitchen, PhotoFix.BrightnessAndColor);
        Assert.True((await ProcessNextAsync()).Succeeded);

        var afterFix = await ClipVersionsAsync(owner);
        // The front-to-foyer clip is untouched; the clip into the kitchen gets a new version for the fixed photo.
        Assert.Equal(original[(front, foyer)], afterFix[(front, foyer)]);
        Assert.Equal(2, afterFix[(foyer, kitchen)].Count);
        var fixedVersion = Assert.Single(afterFix[(foyer, kitchen)].Except(original[(foyer, kitchen)]));
        Assert.Contains("/img-", fixedVersion, StringComparison.Ordinal);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var items = await scope.ServiceProvider.GetRequiredService<IWalkthroughTransitionService>()
                .ListAsync(owner.UserId, owner.PropertyId);
            Assert.All(items, item => Assert.True(item.IsCurrent));

            Assert.True(await scope.ServiceProvider.GetRequiredService<IPropertyPhotoFixService>()
                .RevertAsync(owner.UserId, owner.PropertyId, kitchen));
        }

        // Back on the original photo, the clip made from it is current again, so nothing new is queued.
        Assert.Equal(afterFix, await ClipVersionsAsync(owner));
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var current = await scope.ServiceProvider.GetRequiredService<IWalkthroughTransitionService>()
                .ListAsync(owner.UserId, owner.PropertyId);
            var intoKitchen = current.Single(item => item.ToPropertyMediaId == kitchen);
            var chosen = await dbContext.WalkthroughTransitions.AsNoTracking().SingleAsync(item => item.Id == intoKitchen.TransitionId);
            Assert.Equal(Assert.Single(original[(foyer, kitchen)]), chosen.GenerationVersion);
        }
    }

    private async Task<Dictionary<(Guid From, Guid To), List<string>>> ClipVersionsAsync(OwnerProperty owner)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var rows = await dbContext.WalkthroughTransitions.AsNoTracking()
            .Where(item => item.PropertyId == owner.PropertyId)
            .ToListAsync();
        return rows
            .GroupBy(item => (item.FromPropertyMediaId, item.ToPropertyMediaId))
            .ToDictionary(group => group.Key, group => group.Select(item => item.GenerationVersion).Order().ToList());
    }

    private async Task RequestAsync(OwnerProperty owner, Guid mediaId, PhotoFix fix)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<IPropertyPhotoFixService>()
            .RequestAsync(owner.UserId, owner.PropertyId, mediaId, fix));
    }

    private async Task<PropertyPhotoFixRunResult> ProcessNextAsync()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPropertyPhotoFixProcessor>().ProcessNextAsync()
            ?? throw new Xunit.Sdk.XunitException("No photo fix was waiting.");
    }

    private async Task<PropertyMediaItem> GetMediaAsync(OwnerProperty owner, Guid mediaId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<IPropertyMediaService>()
            .ListAsync(owner.UserId, owner.PropertyId)).Single(item => item.Id == mediaId);
    }

    private async Task<string> BlobPathAsync(Guid mediaId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().PropertyMedia.AsNoTracking()
            .Where(item => item.Id == mediaId)
            .Select(item => item.BlobPath)
            .SingleAsync();
    }

    private async Task<byte[]?> OpenStoredAsync(string blobPath)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        await using var content = await scope.ServiceProvider.GetRequiredService<IPropertyMediaStorage>().OpenReadAsync(blobPath);
        return content is null ? null : await ReadAllAsync(content);
    }

    private async Task<byte[]> ReadPhotoAsync(OwnerProperty owner, Guid mediaId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var media = await scope.ServiceProvider.GetRequiredService<IPropertyMediaService>().OpenReadAsync(owner.UserId, mediaId);
        Assert.NotNull(media);
        await using var content = media.Content;
        return await ReadAllAsync(content);
    }

    private async Task<byte[]> ReadOriginalAsync(OwnerProperty owner, Guid mediaId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var media = await scope.ServiceProvider.GetRequiredService<IPropertyPhotoFixService>()
            .OpenOriginalReadAsync(owner.UserId, mediaId);
        Assert.NotNull(media);
        Assert.Equal("image/png", media.MimeType);
        await using var content = media.Content;
        return await ReadAllAsync(content);
    }

    private static async Task<byte[]> ReadAllAsync(Stream content)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private async Task<Guid> UploadAsync(OwnerProperty owner, string stop)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        await using var content = new MemoryStream(FakePhotoImageCodec.Png);
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
