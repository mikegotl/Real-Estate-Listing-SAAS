using System.Data;
using ListingStudio.Application.Properties;
using ListingStudio.Domain.Properties;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ListingStudio.Infrastructure.Properties;

/// <summary>
/// Breaks a property video down into room photos: sharp stills are pulled from each distinct view, the media
/// analyzer recognizes the room in each, and the best one or two stills per room are added to the photo gallery,
/// already analyzed, so they never need a second AI pass.
/// </summary>
public sealed partial class PropertyVideoFrameExtractionProcessor(
    ApplicationDbContext dbContext,
    IPropertyMediaStorage storage,
    IPropertyVideoFrameExtractor extractor,
    IPropertyMediaAnalyzer analyzer,
    TimeProvider timeProvider,
    ILogger<PropertyVideoFrameExtractionProcessor> logger) : IPropertyVideoFrameExtractionProcessor
{
    // Progress renews the lease, so this only has to outlast the slowest single step.
    private static readonly TimeSpan ExtractionLease = TimeSpan.FromMinutes(20);

    // A frame this close to one the video already gave the gallery is the same still, so a repeated run skips it.
    private const int DuplicateFrameWindowMs = 1_000;

    public async Task<PropertyVideoFrameExtractionRunResult?> ProcessNextAsync(
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var staleBefore = now.Subtract(ExtractionLease);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var candidates = await dbContext.PropertyVideos.FromSqlInterpolated($"""
            SELECT *
            FROM "PropertyVideos"
            WHERE "FrameExtractionAttemptCount" < {IPropertyVideoFrameExtractionProcessor.MaximumAttempts}
              AND (
                "FrameExtractionStatus" = 'Pending'
                OR (
                  "FrameExtractionStatus" = 'Failed'
                  AND ("FrameExtractionNextAttemptAtUtc" IS NULL OR "FrameExtractionNextAttemptAtUtc" <= {now})
                )
                OR (
                  "FrameExtractionStatus" = 'Extracting'
                  AND "FrameExtractionLastAttemptedAtUtc" <= {staleBefore}
                )
              )
            ORDER BY "FrameExtractionRequestedAtUtc", "Id"
            FOR UPDATE SKIP LOCKED
            LIMIT 1
            """).ToListAsync(cancellationToken);
        var video = candidates.SingleOrDefault();
        if (video is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        video.BeginFrameExtraction(now);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var claim = new Claim(
            video.Id,
            video.OrganizationId,
            video.PropertyId,
            video.FrameExtractionAttemptCount,
            video.OriginalFilename);
        // The stabilized, color-corrected version gives crisper stills when it is ready.
        var sourcePath = video.ProcessingStatus == PropertyVideoProcessingStatus.Completed && video.EnhancedBlobPath is not null
            ? video.EnhancedBlobPath
            : video.OriginalBlobPath;
        var sourceFilename = sourcePath == video.OriginalBlobPath ? video.OriginalFilename : "enhanced.mp4";
        dbContext.ChangeTracker.Clear();
        var storedPaths = new List<string>();

        try
        {
            IReadOnlyList<VideoFrameCandidate> frames;
            await using (var content = await storage.OpenReadAsync(sourcePath, cancellationToken)
                ?? throw new FileNotFoundException("The property video could not be opened for room photos."))
            {
                frames = await extractor.ExtractCandidatesAsync(
                    content,
                    sourceFilename,
                    IPropertyVideoFrameExtractionProcessor.MaximumCandidateFrames,
                    cancellationToken);
            }

            if (frames.Count == 0)
            {
                throw new InvalidDataException("The video has no usable frames.");
            }

            var analyzed = await RecognizeRoomsAsync(claim, frames, cancellationToken);
            var rooms = VideoRoomFramePicker.Pick(analyzed);
            await ReportAsync(claim, PropertyVideoFrameExtractionStage.AddingPhotos, 0, 0, cancellationToken);

            var jpegs = frames.ToDictionary(frame => frame.TimestampMs, frame => frame.JpegContent);
            var (added, skipped) = await AddToGalleryAsync(claim, rooms, jpegs, storedPaths, cancellationToken);
            return new PropertyVideoFrameExtractionRunResult(claim.VideoId, true, false, claim.Attempt, added, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await DeleteStoredAsync(storedPaths);
            throw;
        }
        catch (Exception exception)
        {
            await DeleteStoredAsync(storedPaths);
            dbContext.ChangeTracker.Clear();
            LogExtractionFailure(exception, claim.VideoId, claim.Attempt);
            var error = SanitizeError(exception);
            var willRetry = claim.Attempt < IPropertyVideoFrameExtractionProcessor.MaximumAttempts;
            var retryAt = willRetry
                ? timeProvider.GetUtcNow().AddMinutes(Math.Pow(2, claim.Attempt - 1))
                : (DateTimeOffset?)null;
            var claimed = await LoadClaimAsync(claim, cancellationToken);
            claimed.FailFrameExtraction(error, retryAt);
            await dbContext.SaveChangesAsync(cancellationToken);
            return new PropertyVideoFrameExtractionRunResult(claim.VideoId, false, willRetry, claim.Attempt, 0, error);
        }
    }

    private async Task<IReadOnlyList<AnalyzedVideoFrame>> RecognizeRoomsAsync(
        Claim claim,
        IReadOnlyList<VideoFrameCandidate> frames,
        CancellationToken cancellationToken)
    {
        var analyzed = new List<AnalyzedVideoFrame>(frames.Count);
        await ReportAsync(claim, PropertyVideoFrameExtractionStage.RecognizingRooms, 0, frames.Count, cancellationToken);
        for (var index = 0; index < frames.Count; index++)
        {
            var frame = frames[index];
            try
            {
                await using var still = new MemoryStream(frame.JpegContent, writable: false);
                var analysis = await analyzer.AnalyzeAsync(
                    new PropertyMediaAnalysisInput(claim.VideoId, FrameFilename(claim.Filename, frame.TimestampMs), "image/jpeg", still),
                    cancellationToken);
                analyzed.Add(new AnalyzedVideoFrame(frame.TimestampMs, frame.Sharpness, analysis));
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidDataException)
            {
                // One unreadable still costs that view, not the whole video; the run fails only if none are read.
                LogFrameRecognitionFailure(exception, claim.VideoId, frame.TimestampMs);
            }

            await ReportAsync(claim, PropertyVideoFrameExtractionStage.RecognizingRooms, index + 1, frames.Count, cancellationToken);
        }

        return analyzed.Count > 0
            ? analyzed
            : throw new InvalidDataException("No room could be recognized in the video's frames.");
    }

    private async Task<(int Added, int Skipped)> AddToGalleryAsync(
        Claim claim,
        IReadOnlyList<VideoRoom> rooms,
        Dictionary<int, byte[]> jpegs,
        List<string> storedPaths,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var claimed = await LoadClaimAsync(claim, cancellationToken);
        var existing = await dbContext.PropertyMedia.AsNoTracking()
            .Where(media => media.OrganizationId == claim.OrganizationId && media.PropertyId == claim.PropertyId)
            .Select(media => new { media.SourceVideoId, media.SourceVideoTimestampMs })
            .ToListAsync(cancellationToken);
        var alreadyTaken = existing
            .Where(media => media.SourceVideoId == claim.VideoId && media.SourceVideoTimestampMs is not null)
            .Select(media => media.SourceVideoTimestampMs!.Value)
            .ToArray();
        var picks = rooms
            .SelectMany(room => room.Photos.Select(photo => (Room: room, Photo: photo)))
            .Where(pick => !alreadyTaken.Any(taken => Math.Abs(taken - pick.Photo.TimestampMs) < DuplicateFrameWindowMs))
            .OrderBy(pick => pick.Photo.TimestampMs)
            .ToArray();
        var capacity = Math.Max(0, IPropertyMediaService.MaximumMediaPerProperty - existing.Count);
        var accepted = picks.Take(capacity).ToArray();
        var now = timeProvider.GetUtcNow();
        for (var index = 0; index < accepted.Length; index++)
        {
            var (room, photo) = accepted[index];
            var jpeg = jpegs[photo.TimestampMs];
            var image = ImageMetadataReader.Read(jpeg);
            var blobPath = $"organizations/{claim.OrganizationId:N}/properties/{claim.PropertyId:N}/{Guid.NewGuid():N}.jpg";
            await using (var content = new MemoryStream(jpeg, writable: false))
            {
                await storage.StoreAsync(blobPath, content, "image/jpeg", cancellationToken);
            }

            storedPaths.Add(blobPath);
            var media = PropertyMedia.Create(
                claim.OrganizationId,
                claim.PropertyId,
                blobPath,
                FrameFilename(claim.Filename, photo.TimestampMs, room.RoomType),
                "image/jpeg",
                jpeg.Length,
                image.Width,
                image.Height,
                existing.Count + index);
            media.MoveToGallery();
            media.SetSourceVideoFrame(claim.VideoId, photo.TimestampMs);
            media.BeginAnalysis(now);
            media.CompleteAnalysis(photo.Analysis, now);
            dbContext.PropertyMedia.Add(media);
        }

        var skipped = picks.Length - accepted.Length;
        claimed.CompleteFrameExtraction(rooms.Count, accepted.Length, skipped, now);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        storedPaths.Clear();
        return (accepted.Length, skipped);
    }

    private async Task ReportAsync(
        Claim claim,
        PropertyVideoFrameExtractionStage stage,
        int done,
        int total,
        CancellationToken cancellationToken)
    {
        var claimed = await LoadClaimAsync(claim, cancellationToken);
        claimed.ReportFrameExtractionProgress(stage, done, total, timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        dbContext.ChangeTracker.Clear();
    }

    private async Task DeleteStoredAsync(List<string> storedPaths)
    {
        foreach (var path in storedPaths)
        {
            await storage.DeleteAsync(path, CancellationToken.None);
        }

        storedPaths.Clear();
    }

    private async Task<PropertyVideo> LoadClaimAsync(Claim claim, CancellationToken cancellationToken)
    {
        var video = await dbContext.PropertyVideos.SingleOrDefaultAsync(
            candidate => candidate.Id == claim.VideoId && candidate.OrganizationId == claim.OrganizationId,
            cancellationToken);
        if (video is null
            || video.FrameExtractionStatus != PropertyVideoFrameExtractionStatus.Extracting
            || video.FrameExtractionAttemptCount != claim.Attempt)
        {
            throw new InvalidOperationException("The room-photo extraction lease is no longer current.");
        }

        return video;
    }

    /// <summary>"Walkthrough - Kitchen 1m05s.jpg": the source video, the room and where in the video it was taken.</summary>
    internal static string FrameFilename(string videoFilename, int timestampMs, string? roomType = null)
    {
        var video = Path.GetFileNameWithoutExtension(videoFilename).Trim();
        video = video.Length > 120 ? video[..120] : video;
        var room = string.IsNullOrWhiteSpace(roomType) ? "frame" : roomType.Trim();
        room = room.Length > 80 ? room[..80] : room;
        var at = TimeSpan.FromMilliseconds(timestampMs);
        var name = $"{video} - {room} {(int)at.TotalMinutes}m{at.Seconds:00}s";
        return string.Concat(name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '-' : character)) + ".jpg";
    }

    private static string SanitizeError(Exception exception) => exception switch
    {
        FileNotFoundException or IOException => "The property video could not be read.",
        InvalidDataException => "No usable room photos could be found in this video.",
        TimeoutException => "Finding room photos took longer than the processing time limit.",
        HttpRequestException => "The room recognition provider request failed.",
        InvalidOperationException => "Room photos could not be created from this video.",
        _ => "Finding room photos failed unexpectedly.",
    };

    private sealed record Claim(Guid VideoId, Guid OrganizationId, Guid PropertyId, int Attempt, string Filename);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Room-photo extraction for property video {VideoId} attempt {AttemptNumber} failed")]
    private partial void LogExtractionFailure(Exception exception, Guid videoId, int attemptNumber);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Room recognition failed for the frame at {TimestampMs} ms of property video {VideoId}")]
    private partial void LogFrameRecognitionFailure(Exception exception, Guid videoId, int timestampMs);
}
