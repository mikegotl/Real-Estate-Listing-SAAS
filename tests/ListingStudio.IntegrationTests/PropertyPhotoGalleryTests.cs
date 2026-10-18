using ListingStudio.Application.Authentication;
using ListingStudio.Application.Properties;
using ListingStudio.Domain.Notifications;
using ListingStudio.Domain.Properties;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed class PropertyPhotoGalleryTests(PostgreSqlWebApplicationFixture fixture)
    : IClassFixture<PostgreSqlWebApplicationFixture>
{
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Z+VAAAAAASUVORK5CYII=");

    private static readonly byte[] SampleVideo =
        [0, 0, 0, 20, 102, 116, 121, 112, 105, 115, 111, 109, 0, 0, 0, 0];

    [Fact]
    public async Task GalleryPhotosStayOutOfTheWalkthroughUntilPlacedInAStop()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (userId, propertyId) = await CreateOwnerAndPropertyAsync(scope.ServiceProvider, "gallery");
        var media = scope.ServiceProvider.GetRequiredService<IPropertyMediaService>();
        var transitions = scope.ServiceProvider.GetRequiredService<IWalkthroughTransitionService>();

        var front = await UploadAsync(media, userId, propertyId, "front.png", WalkthroughStoryboard.ExteriorFront);
        var spare = await UploadAsync(media, userId, propertyId, "spare.png", null, toGallery: true);
        var kitchen = await UploadAsync(media, userId, propertyId, "kitchen.png", "Kitchen");

        var listed = await media.ListAsync(userId, propertyId);
        Assert.Equal([front, kitchen, spare], listed.Select(item => item.Id));
        Assert.True(listed.Single(item => item.Id == spare).IsInGallery);
        Assert.Null(listed.Single(item => item.Id == spare).WalkthroughStop);
        Assert.Equal([(front, kitchen)], (await transitions.ListAsync(userId, propertyId))
            .Select(pair => (pair.FromPropertyMediaId, pair.ToPropertyMediaId)));

        // Reordering covers the storyboard only; the gallery photo is not part of the walkthrough order.
        await Assert.ThrowsAsync<ArgumentException>(() => media.ReorderAsync(userId, propertyId, [kitchen, front, spare]));
        Assert.True(await media.ReorderAsync(userId, propertyId, [front, kitchen]));

        // Placing the gallery photo in a stop works like an upload there: it joins the end of that stop.
        Assert.True(await media.SetWalkthroughStopAsync(userId, propertyId, spare, WalkthroughStoryboard.ExteriorFront));
        listed = await media.ListAsync(userId, propertyId);
        Assert.Equal([front, spare, kitchen], listed.Select(item => item.Id));
        Assert.All(listed, item => Assert.False(item.IsInGallery));
        Assert.Equal(2, (await transitions.ListAsync(userId, propertyId)).Count);

        Assert.True(await media.MoveToGalleryAsync(userId, propertyId, front));
        listed = await media.ListAsync(userId, propertyId);
        Assert.Equal([spare, kitchen, front], listed.Select(item => item.Id));
        Assert.True(listed.Single(item => item.Id == front).IsInGallery);
        Assert.Equal([(spare, kitchen)], (await transitions.ListAsync(userId, propertyId))
            .Select(pair => (pair.FromPropertyMediaId, pair.ToPropertyMediaId)));

        var properties = scope.ServiceProvider.GetRequiredService<IPropertyService>();
        var summary = (await properties.ListAsync(userId)).Single(property => property.Id == propertyId);
        Assert.Equal(2, summary.PhotoCount);
    }

    [Fact]
    public async Task AGalleryPhotoPlacedInANewIndoorStopJoinsBeforeTheBackyard()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (userId, propertyId) = await CreateOwnerAndPropertyAsync(scope.ServiceProvider, "new-stop");
        var media = scope.ServiceProvider.GetRequiredService<IPropertyMediaService>();

        var front = await UploadAsync(media, userId, propertyId, "front.png", WalkthroughStoryboard.ExteriorFront);
        var kitchen = await UploadAsync(media, userId, propertyId, "kitchen.png", "Kitchen");
        var backyard = await UploadAsync(media, userId, propertyId, "backyard.png", "Backyard");
        var pool = await UploadAsync(media, userId, propertyId, "pool.png", "Pool");
        var bedroom = await UploadAsync(media, userId, propertyId, "bedroom.png", null, toGallery: true);
        var deck = await UploadAsync(media, userId, propertyId, "deck.png", null, toGallery: true);

        // A bedroom found later belongs inside the house, not after the backyard and pool.
        Assert.True(await media.SetWalkthroughStopAsync(userId, propertyId, bedroom, "Bedroom"));
        Assert.Equal(
            [front, kitchen, bedroom, backyard, pool, deck],
            (await media.ListAsync(userId, propertyId)).Select(item => item.Id));

        // An outdoor stop still goes last.
        Assert.True(await media.SetWalkthroughStopAsync(userId, propertyId, deck, "Patio / deck"));
        Assert.Equal(
            [front, kitchen, bedroom, backyard, pool, deck],
            (await media.ListAsync(userId, propertyId)).Select(item => item.Id));
        Assert.Equal(
            ["Exterior front", "Kitchen", "Bedroom", "Backyard", "Pool", "Patio / deck"],
            (await media.ListAsync(userId, propertyId)).Select(item => item.WalkthroughStop));
    }

    [Fact]
    public async Task OtherOrganizationsCannotMovePhotosIntoOrOutOfTheGallery()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var owner = await CreateOwnerAndPropertyAsync(scope.ServiceProvider, "gallery-owner");
        var outsider = await CreateOwnerAndPropertyAsync(scope.ServiceProvider, "gallery-outsider");
        var media = scope.ServiceProvider.GetRequiredService<IPropertyMediaService>();
        var photo = await UploadAsync(media, owner.UserId, owner.PropertyId, "front.png", WalkthroughStoryboard.ExteriorFront);
        var spare = await UploadAsync(media, owner.UserId, owner.PropertyId, "spare.png", null, toGallery: true);

        Assert.False(await media.MoveToGalleryAsync(outsider.UserId, owner.PropertyId, photo));
        Assert.False(await media.MoveToGalleryAsync(outsider.UserId, outsider.PropertyId, photo));
        Assert.False(await media.SetWalkthroughStopAsync(outsider.UserId, outsider.PropertyId, spare, "Kitchen"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            UploadAsync(media, outsider.UserId, owner.PropertyId, "intruder.png", null, toGallery: true));

        var listed = await media.ListAsync(owner.UserId, owner.PropertyId);
        Assert.False(listed.Single(item => item.Id == photo).IsInGallery);
        Assert.True(listed.Single(item => item.Id == spare).IsInGallery);
        Assert.Equal(2, listed.Count);
    }

    [Fact]
    public async Task VideoIsBrokenDownIntoRoomPhotosInTheGallery()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var owner = await CreateOwnerAndPropertyAsync(scope.ServiceProvider, "room-photos");
        var outsider = await CreateOwnerAndPropertyAsync(scope.ServiceProvider, "room-photos-outsider");
        var videos = scope.ServiceProvider.GetRequiredService<IPropertyVideoService>();
        var media = scope.ServiceProvider.GetRequiredService<IPropertyMediaService>();
        var processor = scope.ServiceProvider.GetRequiredService<IPropertyVideoFrameExtractionProcessor>();
        await using var upload = new MemoryStream(SampleVideo);
        var videoId = await videos.UploadAsync(
            owner.UserId,
            owner.PropertyId,
            new PropertyVideoUpload("Walkthrough.mp4", "video/mp4", upload.Length, upload));

        Assert.False(await videos.RequestFrameExtractionAsync(outsider.UserId, owner.PropertyId, videoId));
        Assert.False(await videos.RequestFrameExtractionAsync(outsider.UserId, outsider.PropertyId, videoId));
        Assert.Null(await processor.ProcessNextAsync());

        Assert.True(await videos.RequestFrameExtractionAsync(owner.UserId, owner.PropertyId, videoId));
        Assert.False(await videos.RequestFrameExtractionAsync(owner.UserId, owner.PropertyId, videoId));
        var queued = Assert.Single(await videos.ListAsync(owner.UserId, owner.PropertyId)).FrameExtraction;
        Assert.Equal(PropertyVideoFrameExtractionStatus.Pending, queued!.Status);

        // Five stills: the front, three of the kitchen (the middle one blurry), then the living room.
        fixture.Factory.PropertyVideoFrameExtractor.Enqueue(0, 4_000, 8_000, 12_000, 16_000);
        fixture.Factory.MediaAnalyzer.Enqueue(Analysis(PropertyMediaCategory.FrontExterior, "Front exterior", 85));
        fixture.Factory.MediaAnalyzer.Enqueue(Analysis(PropertyMediaCategory.Kitchen, "Kitchen", 80));
        fixture.Factory.MediaAnalyzer.Enqueue(Analysis(PropertyMediaCategory.Kitchen, "Kitchen", 30));
        fixture.Factory.MediaAnalyzer.Enqueue(Analysis(PropertyMediaCategory.Kitchen, "Kitchen", 90));
        fixture.Factory.MediaAnalyzer.Enqueue(Analysis(PropertyMediaCategory.LivingRoom, "Living room", 75));

        var run = await processor.ProcessNextAsync();

        Assert.NotNull(run);
        Assert.True(run.Succeeded, run.Error);
        Assert.Equal(4, run.PhotoCount);
        Assert.Equal(
            IPropertyVideoFrameExtractionProcessor.MaximumCandidateFrames,
            fixture.Factory.PropertyVideoFrameExtractor.LastMaximumCandidates);
        var finished = Assert.Single(await videos.ListAsync(owner.UserId, owner.PropertyId)).FrameExtraction!;
        Assert.Equal(PropertyVideoFrameExtractionStatus.Completed, finished.Status);
        Assert.Equal((3, 4, 0), (finished.RoomCount, finished.PhotoCount, finished.SkippedPhotoCount));
        var found = Assert.Single((await NotificationInbox.ForAsync(fixture, owner.UserId)).Items);
        Assert.Equal((UserNotificationKind.RoomPhotos, UserNotificationOutcome.Succeeded), (found.Kind, found.Outcome));
        Assert.Equal("4 room photos from \"Walkthrough.mp4\" are in Extra photos for 123 Main Street.", found.Message);
        Assert.Empty((await NotificationInbox.ForAsync(fixture, outsider.UserId)).Items);

        var gallery = await media.ListAsync(owner.UserId, owner.PropertyId);
        Assert.All(gallery, item =>
        {
            Assert.True(item.IsInGallery);
            Assert.Equal(videoId, item.SourceVideoId);
            Assert.Equal(PropertyMediaAnalysisStatus.Completed, item.AnalysisStatus);
            Assert.Equal("image/jpeg", item.MimeType);
            Assert.Equal((8, 6), (item.Width, item.Height));
        });
        Assert.Equal([0, 4_000, 12_000, 16_000], gallery.Select(item => item.SourceVideoTimestampMs));
        Assert.Equal("Walkthrough - Kitchen 0m12s.jpg", gallery[2].OriginalFilename);
        Assert.Empty(await media.ListAsync(outsider.UserId, owner.PropertyId));
        var still = await media.OpenReadAsync(owner.UserId, gallery[0].Id);
        Assert.NotNull(still);
        await still.Content.DisposeAsync();

        // Asking again only adds stills the gallery does not already have from this video.
        Assert.True(await videos.RequestFrameExtractionAsync(owner.UserId, owner.PropertyId, videoId));
        fixture.Factory.PropertyVideoFrameExtractor.Enqueue(400, 20_000);
        fixture.Factory.MediaAnalyzer.Enqueue(Analysis(PropertyMediaCategory.FrontExterior, "Front exterior", 85));
        fixture.Factory.MediaAnalyzer.Enqueue(Analysis(PropertyMediaCategory.Backyard, "Backyard", 70));
        var again = await processor.ProcessNextAsync();
        Assert.True(again!.Succeeded, again.Error);
        Assert.Equal(1, again.PhotoCount);
        // The test clock does not move between the two runs, so both notes share a timestamp; look for the second by text.
        Assert.Contains(
            "1 room photo from \"Walkthrough.mp4\" is in Extra photos for 123 Main Street.",
            (await NotificationInbox.ForAsync(fixture, owner.UserId)).Items.Select(item => item.Message));
        Assert.Equal(5, (await media.ListAsync(owner.UserId, owner.PropertyId)).Count);

        // Placed room photos join the walkthrough like any upload.
        Assert.True(await media.SetWalkthroughStopAsync(owner.UserId, owner.PropertyId, gallery[0].Id, WalkthroughStoryboard.ExteriorFront));
        Assert.False((await media.ListAsync(owner.UserId, owner.PropertyId)).Single(item => item.Id == gallery[0].Id).IsInGallery);
    }

    [Fact]
    public async Task AFailedRoomPhotoRunAddsNothingAndCanRetry()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var owner = await CreateOwnerAndPropertyAsync(scope.ServiceProvider, "room-photos-retry");
        var videos = scope.ServiceProvider.GetRequiredService<IPropertyVideoService>();
        var media = scope.ServiceProvider.GetRequiredService<IPropertyMediaService>();
        var processor = scope.ServiceProvider.GetRequiredService<IPropertyVideoFrameExtractionProcessor>();
        await using var upload = new MemoryStream(SampleVideo);
        var videoId = await videos.UploadAsync(
            owner.UserId,
            owner.PropertyId,
            new PropertyVideoUpload("tour.mp4", "video/mp4", upload.Length, upload));
        Assert.True(await videos.RequestFrameExtractionAsync(owner.UserId, owner.PropertyId, videoId));

        fixture.Factory.PropertyVideoFrameExtractor.Enqueue(new TimeoutException("too slow"));
        var failed = await processor.ProcessNextAsync();

        Assert.NotNull(failed);
        Assert.False(failed.Succeeded);
        Assert.True(failed.WillRetry);
        Assert.Empty((await NotificationInbox.ForAsync(fixture, owner.UserId)).Items);
        var state = Assert.Single(await videos.ListAsync(owner.UserId, owner.PropertyId)).FrameExtraction!;
        Assert.Equal(PropertyVideoFrameExtractionStatus.Failed, state.Status);
        Assert.Equal("Finding room photos took longer than the processing time limit.", state.LastError);
        Assert.Empty(await media.ListAsync(owner.UserId, owner.PropertyId));

        // The agent can ask again straight away rather than waiting for the automatic retry.
        Assert.True(await videos.RequestFrameExtractionAsync(owner.UserId, owner.PropertyId, videoId));
        fixture.Factory.PropertyVideoFrameExtractor.Enqueue(1_000);
        fixture.Factory.MediaAnalyzer.Enqueue(new HttpRequestException("vision unavailable"));
        var unrecognized = await processor.ProcessNextAsync();
        Assert.False(unrecognized!.Succeeded);
        Assert.Equal(
            "No usable room photos could be found in this video.",
            Assert.Single(await videos.ListAsync(owner.UserId, owner.PropertyId)).FrameExtraction!.LastError);
        Assert.Empty(await media.ListAsync(owner.UserId, owner.PropertyId));
    }

    private static PropertyMediaAnalysis Analysis(PropertyMediaCategory category, string room, int quality) => new(
        category,
        room,
        quality,
        60,
        category == PropertyMediaCategory.FrontExterior,
        category != PropertyMediaCategory.FrontExterior,
        false,
        [],
        $"A still of the {room.ToLowerInvariant()}.",
        0);

    private static async Task<Guid> UploadAsync(
        IPropertyMediaService media,
        string userId,
        Guid propertyId,
        string filename,
        string? stop,
        bool toGallery = false)
    {
        await using var content = new MemoryStream(OnePixelPng);
        return await media.UploadAsync(
            userId,
            propertyId,
            new PropertyMediaUpload(filename, "image/png", content.Length, content, stop, toGallery));
    }

    private static async Task<(string UserId, Guid PropertyId)> CreateOwnerAndPropertyAsync(
        IServiceProvider services,
        string prefix)
    {
        var registration = services.GetRequiredService<IAccountRegistrationService>();
        var properties = services.GetRequiredService<IPropertyService>();
        var registrationResult = await registration.RegisterAsync(new RegisterAccountCommand(
            $"{prefix}-{Guid.NewGuid():N}@example.com",
            "Password123",
            $"{prefix} Realty {Guid.NewGuid():N}"));
        Assert.True(registrationResult.Succeeded, string.Join(", ", registrationResult.Errors));
        var propertyId = await properties.CreateAsync(registrationResult.UserId!, new PropertyInput(
            "123 Main Street",
            null,
            "Raleigh",
            "NC",
            "27601",
            450_000m,
            3,
            2.5m,
            2_100,
            0.25m,
            1998,
            PropertyType.SingleFamily,
            "A comfortable home.",
            ListingStatus.Draft));
        return (registrationResult.UserId!, propertyId);
    }
}
