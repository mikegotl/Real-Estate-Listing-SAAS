using System.Data;
using ListingStudio.Application.Properties;
using ListingStudio.Domain.Properties;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ListingStudio.Infrastructure.Properties;

public sealed partial class PropertyMediaAnalysisProcessor(
    ApplicationDbContext dbContext,
    IPropertyMediaStorage storage,
    IPropertyMediaAnalyzer analyzer,
    TimeProvider timeProvider,
    ILogger<PropertyMediaAnalysisProcessor> logger) : IPropertyMediaAnalysisProcessor
{
    private static readonly TimeSpan AnalysisLease = TimeSpan.FromMinutes(10);

    public async Task<PropertyMediaAnalysisRunResult?> AnalyzeNextAsync(
        CancellationToken cancellationToken = default) =>
        await AnalyzeNextCoreAsync(null, null, cancellationToken);

    public async Task<PropertyMediaAnalysisRunResult?> AnalyzeNextForPropertyAsync(
        Guid organizationId,
        Guid propertyId,
        CancellationToken cancellationToken = default)
    {
        if (organizationId == Guid.Empty || propertyId == Guid.Empty)
        {
            throw new ArgumentException("Organization and property identities are required.");
        }

        return await AnalyzeNextCoreAsync(organizationId, propertyId, cancellationToken);
    }

    private async Task<PropertyMediaAnalysisRunResult?> AnalyzeNextCoreAsync(
        Guid? targetOrganizationId,
        Guid? targetPropertyId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var staleBefore = now.Subtract(AnalysisLease);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        var candidates = targetOrganizationId is null
            ? await dbContext.PropertyMedia.FromSqlInterpolated($"""
                SELECT *
                FROM "PropertyMedia"
                WHERE "AnalysisAttemptCount" < {IPropertyMediaAnalysisProcessor.MaximumAttempts}
                  AND (
                    "AnalysisStatus" = 'Pending'
                    OR (
                      "AnalysisStatus" = 'Failed'
                      AND ("AnalysisNextAttemptAtUtc" IS NULL OR "AnalysisNextAttemptAtUtc" <= {now})
                    )
                    OR (
                      "AnalysisStatus" = 'Analyzing'
                      AND "AnalysisLastAttemptedAtUtc" <= {staleBefore}
                    )
                  )
                ORDER BY "UploadedAt", "Id"
                FOR UPDATE SKIP LOCKED
                LIMIT 1
                """).ToListAsync(cancellationToken)
            : await dbContext.PropertyMedia.FromSqlInterpolated($"""
                SELECT *
                FROM "PropertyMedia"
                WHERE "OrganizationId" = {targetOrganizationId.Value}
                  AND "PropertyId" = {targetPropertyId!.Value}
                  AND "AnalysisAttemptCount" < {IPropertyMediaAnalysisProcessor.MaximumAttempts}
                  AND (
                    "AnalysisStatus" = 'Pending'
                    OR (
                      "AnalysisStatus" = 'Failed'
                      AND ("AnalysisNextAttemptAtUtc" IS NULL OR "AnalysisNextAttemptAtUtc" <= {now})
                    )
                    OR (
                      "AnalysisStatus" = 'Analyzing'
                      AND "AnalysisLastAttemptedAtUtc" <= {staleBefore}
                    )
                  )
                ORDER BY "UploadedAt", "Id"
                FOR UPDATE SKIP LOCKED
                LIMIT 1
                """).ToListAsync(cancellationToken);
        var media = candidates.SingleOrDefault();
        if (media is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        media.BeginAnalysis(now);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var mediaId = media.Id;
        var organizationId = media.OrganizationId;
        var attemptNumber = media.AnalysisAttemptCount;
        var blobPath = media.BlobPath;
        var originalFilename = media.OriginalFilename;
        var mimeType = media.MimeType;
        dbContext.ChangeTracker.Clear();

        try
        {
            await using var content = await storage.OpenReadAsync(blobPath, cancellationToken)
                ?? throw new FileNotFoundException("The original property image could not be opened for analysis.");
            var analysis = await analyzer.AnalyzeAsync(
                new PropertyMediaAnalysisInput(mediaId, originalFilename, mimeType, content),
                cancellationToken);

            var claimed = await LoadClaimAsync(organizationId, mediaId, attemptNumber, cancellationToken);
            claimed.CompleteAnalysis(analysis, timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(cancellationToken);
            return new PropertyMediaAnalysisRunResult(mediaId, true, false, attemptNumber, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            dbContext.ChangeTracker.Clear();
            LogAnalysisFailure(exception, mediaId, attemptNumber);
            var error = SanitizeError(exception);
            var willRetry = attemptNumber < IPropertyMediaAnalysisProcessor.MaximumAttempts;
            var retryAt = willRetry
                ? timeProvider.GetUtcNow().AddMinutes(Math.Pow(2, attemptNumber - 1))
                : (DateTimeOffset?)null;
            var claimed = await LoadClaimAsync(organizationId, mediaId, attemptNumber, cancellationToken);
            claimed.FailAnalysis(error, retryAt);
            await dbContext.SaveChangesAsync(cancellationToken);
            return new PropertyMediaAnalysisRunResult(mediaId, false, willRetry, attemptNumber, error);
        }
    }

    private async Task<PropertyMedia> LoadClaimAsync(
        Guid organizationId,
        Guid mediaId,
        int attemptNumber,
        CancellationToken cancellationToken)
    {
        var media = await dbContext.PropertyMedia.SingleOrDefaultAsync(
            candidate => candidate.Id == mediaId && candidate.OrganizationId == organizationId,
            cancellationToken);
        if (media is null
            || media.AnalysisStatus != PropertyMediaAnalysisStatus.Analyzing
            || media.AnalysisAttemptCount != attemptNumber)
        {
            throw new InvalidOperationException("The property media analysis lease is no longer current.");
        }

        return media;
    }

    private static string SanitizeError(Exception exception) => exception switch
    {
        FileNotFoundException or IOException => "The original property image could not be read.",
        HttpRequestException => "The media analysis provider request failed.",
        InvalidDataException => "The media analysis provider returned an invalid response.",
        InvalidOperationException => "Media analysis could not be completed.",
        _ => "Property media analysis failed unexpectedly.",
    };

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Property media {MediaId} analysis attempt {AttemptNumber} failed")]
    private partial void LogAnalysisFailure(Exception exception, Guid mediaId, int attemptNumber);
}
