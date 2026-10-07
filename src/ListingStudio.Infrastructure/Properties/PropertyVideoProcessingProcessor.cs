using System.Data;
using ListingStudio.Application.Properties;
using ListingStudio.Domain.Properties;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ListingStudio.Infrastructure.Properties;

public sealed partial class PropertyVideoProcessingProcessor(
    ApplicationDbContext dbContext,
    IPropertyMediaStorage storage,
    IPropertyVideoTranscoder transcoder,
    TimeProvider timeProvider,
    ILogger<PropertyVideoProcessingProcessor> logger) : IPropertyVideoProcessingProcessor
{
    private static readonly TimeSpan ProcessingLease = TimeSpan.FromMinutes(30);

    public async Task<PropertyVideoProcessingRunResult?> ProcessNextAsync(
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var staleBefore = now.Subtract(ProcessingLease);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var candidates = await dbContext.PropertyVideos.FromSqlInterpolated($"""
            SELECT *
            FROM "PropertyVideos"
            WHERE "ProcessingAttemptCount" < {IPropertyVideoProcessingProcessor.MaximumAttempts}
              AND (
                "ProcessingStatus" = 'Pending'
                OR (
                  "ProcessingStatus" = 'Failed'
                  AND ("ProcessingNextAttemptAtUtc" IS NULL OR "ProcessingNextAttemptAtUtc" <= {now})
                )
                OR (
                  "ProcessingStatus" = 'Processing'
                  AND "ProcessingLastAttemptedAtUtc" <= {staleBefore}
                )
              )
            ORDER BY "UploadedAtUtc", "Id"
            FOR UPDATE SKIP LOCKED
            LIMIT 1
            """).ToListAsync(cancellationToken);
        var video = candidates.SingleOrDefault();
        if (video is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        video.BeginProcessing(now);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var videoId = video.Id;
        var organizationId = video.OrganizationId;
        var propertyId = video.PropertyId;
        var attemptNumber = video.ProcessingAttemptCount;
        var originalPath = video.OriginalBlobPath;
        var originalFilename = video.OriginalFilename;
        var enhancedPath = $"organizations/{organizationId:N}/properties/{propertyId:N}/videos/{videoId:N}/{transcoder.EnhancementVersion}.mp4";
        dbContext.ChangeTracker.Clear();
        var outputStored = false;

        try
        {
            await using var original = await storage.OpenReadAsync(originalPath, cancellationToken)
                ?? throw new FileNotFoundException("The original property video could not be opened for processing.");
            var result = await transcoder.EnhanceAsync(original, originalFilename, cancellationToken);
            await using (result.Content)
            {
                await storage.DeleteAsync(enhancedPath, cancellationToken);
                await storage.StoreAsync(enhancedPath, result.Content, "video/mp4", cancellationToken);
                outputStored = true;
            }

            var claimed = await LoadClaimAsync(organizationId, videoId, attemptNumber, cancellationToken);
            claimed.CompleteProcessing(
                enhancedPath,
                result.FileSize,
                result.Metadata.Width,
                result.Metadata.Height,
                result.Metadata.DurationMs,
                result.Metadata.FrameRate,
                transcoder.EnhancementVersion,
                timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(cancellationToken);
            return new PropertyVideoProcessingRunResult(videoId, true, false, attemptNumber, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (outputStored)
            {
                await storage.DeleteAsync(enhancedPath, CancellationToken.None);
            }

            dbContext.ChangeTracker.Clear();
            LogProcessingFailure(exception, videoId, attemptNumber);
            var error = SanitizeError(exception);
            var willRetry = attemptNumber < IPropertyVideoProcessingProcessor.MaximumAttempts;
            var retryAt = willRetry
                ? timeProvider.GetUtcNow().AddMinutes(Math.Pow(2, attemptNumber - 1))
                : (DateTimeOffset?)null;
            var claimed = await LoadClaimAsync(organizationId, videoId, attemptNumber, cancellationToken);
            claimed.FailProcessing(error, retryAt);
            await dbContext.SaveChangesAsync(cancellationToken);
            return new PropertyVideoProcessingRunResult(videoId, false, willRetry, attemptNumber, error);
        }
    }

    private async Task<PropertyVideo> LoadClaimAsync(
        Guid organizationId,
        Guid videoId,
        int attemptNumber,
        CancellationToken cancellationToken)
    {
        var video = await dbContext.PropertyVideos.SingleOrDefaultAsync(
            candidate => candidate.Id == videoId && candidate.OrganizationId == organizationId,
            cancellationToken);
        if (video is null
            || video.ProcessingStatus != PropertyVideoProcessingStatus.Processing
            || video.ProcessingAttemptCount != attemptNumber)
        {
            throw new InvalidOperationException("The property video processing lease is no longer current.");
        }

        return video;
    }

    private static string SanitizeError(Exception exception) => exception switch
    {
        FileNotFoundException or IOException => "The original property video could not be read or written.",
        InvalidDataException => "The video processor returned an invalid result.",
        TimeoutException => "Video enhancement exceeded the processing time limit.",
        InvalidOperationException => "Video enhancement could not be completed.",
        _ => "Property video enhancement failed unexpectedly.",
    };

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Property video {VideoId} processing attempt {AttemptNumber} failed")]
    private partial void LogProcessingFailure(Exception exception, Guid videoId, int attemptNumber);
}
