using ListingStudio.Application.Properties;
using ListingStudio.Domain.Properties;
using ListingStudio.Video.Processing;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class VideoRoomPhotoTests
{
    [Fact]
    public void ConsecutiveStillsOfTheSameRoomBecomeOneRoomWithItsTwoBestViews()
    {
        var rooms = VideoRoomFramePicker.Pick(
        [
            Frame(0, PropertyMediaCategory.FrontExterior, quality: 80),
            Frame(4_000, PropertyMediaCategory.Kitchen, quality: 60),
            Frame(8_000, PropertyMediaCategory.Kitchen, quality: 90),
            Frame(12_000, PropertyMediaCategory.Kitchen, quality: 85),
            Frame(16_000, PropertyMediaCategory.LivingRoom, quality: 70),
        ]);

        Assert.Collection(
            rooms,
            exterior => Assert.Equal([0], exterior.Photos.Select(photo => photo.TimestampMs)),
            kitchen =>
            {
                Assert.Equal(PropertyMediaCategory.Kitchen, kitchen.Category);
                Assert.Equal([8_000, 12_000], kitchen.Photos.Select(photo => photo.TimestampMs));
            },
            living => Assert.Single(living.Photos));
    }

    [Fact]
    public void ASecondPhotoMustBeADistinctUsableView()
    {
        var rooms = VideoRoomFramePicker.Pick(
        [
            Frame(10_000, PropertyMediaCategory.Bedroom, quality: 90),
            Frame(10_500, PropertyMediaCategory.Bedroom, quality: 88),
            Frame(14_000, PropertyMediaCategory.Bedroom, quality: 20),
        ]);

        var bedroom = Assert.Single(rooms);
        Assert.Equal([10_000], bedroom.Photos.Select(photo => photo.TimestampMs));
    }

    [Fact]
    public void ALoneMisrecognizedStillInsideARoomDoesNotSplitIt()
    {
        var rooms = VideoRoomFramePicker.Pick(
        [
            Frame(0, PropertyMediaCategory.Kitchen, quality: 70),
            Frame(4_000, PropertyMediaCategory.DiningRoom, quality: 95),
            Frame(8_000, PropertyMediaCategory.Kitchen, quality: 75),
            Frame(12_000, PropertyMediaCategory.Bathroom, quality: 65),
        ]);

        Assert.Equal(
            [PropertyMediaCategory.Kitchen, PropertyMediaCategory.Bathroom],
            rooms.Select(room => room.Category));
        Assert.Equal(2, rooms[0].Photos.Count);
    }

    [Fact]
    public void StillsWithoutPeopleWinAndOtherRoomsMatchByName()
    {
        var rooms = VideoRoomFramePicker.Pick(
        [
            Frame(0, PropertyMediaCategory.Other, quality: 95, roomType: "Wine cellar", people: true),
            Frame(3_000, PropertyMediaCategory.Other, quality: 60, roomType: "wine cellar "),
            Frame(6_000, PropertyMediaCategory.Other, quality: 70, roomType: "Sunroom"),
        ]);

        Assert.Equal(2, rooms.Count);
        Assert.Equal([3_000], rooms[0].Photos.Select(photo => photo.TimestampMs));
        Assert.Equal("Sunroom", rooms[1].RoomType);
    }

    [Fact]
    public void SharpnessBreaksTiesBetweenStillsOfEqualQuality()
    {
        var rooms = VideoRoomFramePicker.Pick(
        [
            Frame(0, PropertyMediaCategory.Office, quality: 80, sharpness: 10),
            Frame(500, PropertyMediaCategory.Office, quality: 80, sharpness: 900),
        ]);

        Assert.Equal([500], Assert.Single(rooms).Photos.Select(photo => photo.TimestampMs));
    }

    [Fact]
    public void SamplingSplitsAtCutsAndPicksTheSharpestLitSampleOfEachView()
    {
        // Two hard cuts give three shots of 3 seconds each (6 samples); the sharpest inner sample of each wins.
        var samples = Enumerable.Range(0, 18)
            .Select(index => new FrameSample(
                Sharpness: index is 2 or 9 or 15 ? 500 : 50,
                Difference: index is 6 or 12 ? 60 : 2,
                Brightness: 120))
            .ToArray();

        Assert.Equal([2, 9, 15], VideoFrameSampling.ChooseFrames(samples, maximumCandidates: 24));
    }

    [Fact]
    public void ALongContinuousShotIsSplitIntoSeveralViews()
    {
        // A 20-second pan without a cut: 40 samples become 5 views of at most 4 seconds.
        var samples = Enumerable.Range(0, 40).Select(_ => new FrameSample(100, 3, 120)).ToArray();

        Assert.Equal(5, VideoFrameSampling.ChooseFrames(samples, maximumCandidates: 24).Count);
        Assert.Equal(3, VideoFrameSampling.ChooseFrames(samples, maximumCandidates: 3).Count);
    }

    [Fact]
    public void SteadyPanningIsNotMistakenForCuts()
    {
        // Walking through a room changes every sample a lot; only the jump to the next room is a cut.
        var samples = Enumerable.Range(0, 16)
            .Select(index => new FrameSample(
                Sharpness: index is 3 or 12 ? 400 : 50,
                Difference: index == 0 ? 0 : index == 8 ? 95 : 32,
                Brightness: 120))
            .ToArray();

        Assert.Equal([3, 12], VideoFrameSampling.ChooseFrames(samples, maximumCandidates: 24));
    }

    [Fact]
    public void DarkSamplesAndPassingGlimpsesAreNotChosen()
    {
        var samples = Enumerable.Range(0, 12)
            .Select(index => new FrameSample(
                Sharpness: 100,
                Difference: index is 4 or 5 ? 80 : 1,
                Brightness: index < 4 ? 5 : 140))
            .ToArray();

        // Samples 0-3 are a fade from black, sample 4 a half-second glimpse; only the view from sample 5 remains.
        var chosen = VideoFrameSampling.ChooseFrames(samples, maximumCandidates: 24);

        Assert.All(chosen, index => Assert.True(index >= 5));
        Assert.NotEmpty(chosen);
    }

    [Fact]
    public void SharpEdgesMeasureSharperThanAFlatFrameAndChangesAreMeasured()
    {
        const int width = 16;
        const int height = 16;
        var flat = Enumerable.Repeat((byte)128, width * height).ToArray();
        var checkered = Enumerable.Range(0, width * height)
            .Select(index => (byte)(((index % width) + (index / width)) % 2 == 0 ? 255 : 0))
            .ToArray();

        var flatSample = VideoFrameSampling.Measure(flat, [], width, height);
        var sharpSample = VideoFrameSampling.Measure(checkered, flat, width, height);

        Assert.Equal(0, flatSample.Sharpness);
        Assert.True(sharpSample.Sharpness > 1_000);
        Assert.Equal(0, flatSample.Difference);
        Assert.True(sharpSample.Difference > 100);
    }

    [Fact]
    public void StillCommandSeeksToTheChosenMomentAndCapsTheWidth()
    {
        var arguments = FfmpegPropertyVideoFrameExtractor.BuildStillArguments("in.mp4", "still.jpg", 12_500).ToList();

        Assert.Equal("12.5", arguments[arguments.IndexOf("-ss") + 1]);
        Assert.True(arguments.IndexOf("-ss") < arguments.IndexOf("-i"));
        Assert.Contains("min(1920,iw)", arguments[arguments.IndexOf("-vf") + 1], StringComparison.Ordinal);
        Assert.Equal("1", arguments[arguments.IndexOf("-frames:v") + 1]);
    }

    [Fact]
    public void PlacingAGalleryPhotoInAStopTakesItOutOfTheGallery()
    {
        var media = PropertyMedia.Create(Guid.NewGuid(), Guid.NewGuid(), "path.jpg", "photo.jpg", "image/jpeg", 10, 4, 3, 0);
        media.SetWalkthroughStop("Kitchen");

        media.MoveToGallery();
        Assert.True(media.IsInGallery);
        Assert.Null(media.WalkthroughStop);

        media.SetWalkthroughStop(null);
        Assert.True(media.IsInGallery);

        media.SetWalkthroughStop("Living room");
        Assert.False(media.IsInGallery);
        Assert.Equal("Living room", media.WalkthroughStop);
    }

    [Fact]
    public void FrameExtractionMovesThroughItsStatesAndCanBeRequestedAgain()
    {
        var video = PropertyVideo.Create(Guid.NewGuid(), Guid.NewGuid(), "v.mp4", "tour.mp4", "video/mp4", 10, 1920, 1080, 30_000, 30, true);
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(PropertyVideoFrameExtractionStatus.NotRequested, video.FrameExtractionStatus);
        Assert.Throws<InvalidOperationException>(() => video.BeginFrameExtraction(now));

        video.RequestFrameExtraction(now);
        Assert.Throws<InvalidOperationException>(() => video.RequestFrameExtraction(now));
        video.BeginFrameExtraction(now);
        video.ReportFrameExtractionProgress(PropertyVideoFrameExtractionStage.RecognizingRooms, 3, 10, now);
        Assert.Equal((3, 10), (video.FrameExtractionStepsDone, video.FrameExtractionStepsTotal));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            video.ReportFrameExtractionProgress(PropertyVideoFrameExtractionStage.RecognizingRooms, 11, 10, now));

        video.CompleteFrameExtraction(roomCount: 5, photoCount: 8, skippedPhotoCount: 1, now);
        Assert.Equal(PropertyVideoFrameExtractionStatus.Completed, video.FrameExtractionStatus);
        Assert.Equal(8, video.FrameExtractionPhotoCount);

        video.RequestFrameExtraction(now);
        Assert.Equal(PropertyVideoFrameExtractionStatus.Pending, video.FrameExtractionStatus);
        Assert.Null(video.FrameExtractionPhotoCount);
    }

    private static AnalyzedVideoFrame Frame(
        int timestampMs,
        PropertyMediaCategory category,
        int quality,
        string? roomType = null,
        bool people = false,
        double sharpness = 100) => new(
            timestampMs,
            sharpness,
            new PropertyMediaAnalysis(
                category,
                roomType ?? category.ToString(),
                quality,
                50,
                category == PropertyMediaCategory.FrontExterior,
                category != PropertyMediaCategory.FrontExterior,
                people,
                [],
                "A recognized room.",
                0));
}
