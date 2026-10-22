using System.Data;
using System.Data.Common;
using ListingStudio.Application.Administration;
using ListingStudio.Application.Properties;
using ListingStudio.Domain.Properties;
using ListingStudio.Domain.Notifications;
using ListingStudio.Infrastructure.Notifications;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ListingStudio.Infrastructure.Properties;

/// <summary>
/// Makes fixed photos. Brightness/color and leveling/cropping are measured and applied locally; Remove clutter sends
/// the photo, padded onto the editor's canvas, to the AI clutter remover and cuts the result back to the photo's own proportions.
/// The fixed photo replaces the photo it was made from, keeping the upload as the original.
/// </summary>
public sealed partial class PropertyPhotoFixProcessor(
    ApplicationDbContext dbContext,
    IPropertyMediaStorage storage,
    IPhotoImageCodec codec,
    IPhotoClutterRemover clutterRemover,
    IAiUsagePropertyContext aiUsagePropertyContext,
    IWalkthroughTransitionService walkthroughTransitions,
    TimeProvider timeProvider,
    ILogger<PropertyPhotoFixProcessor> logger) : IPropertyPhotoFixProcessor
{
    /// <summary>The longest side of a locally fixed result; larger photos are scaled down to it.</summary>
    public const int MaximumFixedDimension = 6_000;

    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(10);

    public async Task<PropertyPhotoFixRunResult?> ProcessNextAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var staleBefore = now.Subtract(Lease);
        PropertyPhotoFix fix;
        await using (var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken))
        {
            var candidates = await dbContext.PropertyPhotoFixes.FromSqlInterpolated($"""
                SELECT *
                FROM "PropertyPhotoFixes"
                WHERE "AttemptCount" < {PropertyPhotoFix.MaximumAttempts}
                  AND (
                    "Status" = 'Pending'
                    OR ("Status" = 'Failed' AND "NextAttemptAtUtc" IS NOT NULL AND "NextAttemptAtUtc" <= {now})
                    OR ("Status" = 'Processing' AND "LastAttemptedAtUtc" <= {staleBefore})
                  )
                ORDER BY "RequestedAtUtc", "Id"
                FOR UPDATE SKIP LOCKED
                LIMIT 1
                """).ToListAsync(cancellationToken);
            var claimed = candidates.SingleOrDefault();
            if (claimed is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return null;
            }

            claimed.Begin(now);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            fix = claimed;
        }

        var claim = new Claim(fix.Id, fix.OrganizationId, fix.PropertyId, fix.PropertyMediaId, fix.Kind, fix.SourceBlobPath, fix.AttemptCount);
        dbContext.ChangeTracker.Clear();
        string? storedPath = null;
        try
        {
            var source = await dbContext.PropertyMedia.AsNoTracking().SingleOrDefaultAsync(
                media => media.Id == claim.MediaId && media.OrganizationId == claim.OrganizationId,
                cancellationToken);
            if (source is null || source.BlobPath != claim.SourceBlobPath)
            {
                throw new PhotoChangedException();
            }

            byte[] original;
            await using (var content = await storage.OpenReadAsync(source.BlobPath, cancellationToken)
                ?? throw new FileNotFoundException("The photo could not be opened."))
            using (var buffer = new MemoryStream())
            {
                await content.CopyToAsync(buffer, cancellationToken);
                original = buffer.ToArray();
            }

            PhotoPixels fixedPhoto;
            using (aiUsagePropertyContext.BeginProperty(claim.PropertyId))
            {
                fixedPhoto = claim.Kind switch
                {
                    PhotoFix.BrightnessAndColor => await NormalizeAsync(original, source.MimeType, cancellationToken),
                    PhotoFix.RemoveClutter => await RemoveClutterAsync(original, source.MimeType, cancellationToken),
                    PhotoFix.LevelAndCrop => await LevelAndCropAsync(original, source.MimeType, cancellationToken),
                    _ => throw new InvalidOperationException("The photo fix is not supported."),
                };
            }
            var jpeg = await codec.EncodeJpegAsync(fixedPhoto, cancellationToken);
            storedPath = $"organizations/{claim.OrganizationId:N}/properties/{claim.PropertyId:N}/{Guid.NewGuid():N}.jpg";
            await using (var stored = new MemoryStream(jpeg, writable: false))
            {
                await storage.StoreAsync(storedPath, stored, "image/jpeg", cancellationToken);
            }

            var superseded = await ApplyAsync(claim, storedPath, jpeg.Length, fixedPhoto, cancellationToken);
            storedPath = null;
            if (superseded is not null)
            {
                await storage.DeleteAsync(superseded, cancellationToken);
            }

            await RequeueAffectedClipsAsync(claim, cancellationToken);
            return new PropertyPhotoFixRunResult(claim.FixId, claim.MediaId, claim.Kind, true, false, claim.Attempt, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await DeleteStoredAsync(storedPath);
            throw;
        }
        catch (Exception exception)
        {
            await DeleteStoredAsync(storedPath);
            dbContext.ChangeTracker.Clear();
            LogFixFailure(exception, claim.FixId, claim.MediaId, claim.Kind, claim.Attempt);
            // Only a dropped connection or a timeout is worth paying for again; a rejected edit fails the same way.
            var transient = exception is HttpRequestException or TimeoutException or IOException
                && exception is not FileNotFoundException;
            var retryAt = transient
                ? timeProvider.GetUtcNow().AddMinutes(Math.Pow(2, claim.Attempt - 1))
                : (DateTimeOffset?)null;
            var error = SanitizeError(exception);
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);
            var claimed = await dbContext.PropertyPhotoFixes.SingleOrDefaultAsync(
                candidate => candidate.Id == claim.FixId
                    && candidate.OrganizationId == claim.OrganizationId
                    && candidate.Status == PropertyPhotoFixStatus.Processing
                    && candidate.AttemptCount == claim.Attempt,
                cancellationToken);
            if (claimed is null)
            {
                // The photo was deleted, taking its fixes with it, or another worker reclaimed this one.
                return new PropertyPhotoFixRunResult(claim.FixId, claim.MediaId, claim.Kind, false, false, claim.Attempt, error);
            }

            claimed.Fail(error, retryAt);
            if (claimed.NextAttemptAtUtc is null)
            {
                await NotifyIfPropertyFinishedAsync(claimed, cancellationToken);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new PropertyPhotoFixRunResult(
                claim.FixId,
                claim.MediaId,
                claim.Kind,
                false,
                claimed.NextAttemptAtUtc is not null,
                claim.Attempt,
                error);
        }
    }

    private async Task<PhotoPixels> NormalizeAsync(byte[] original, string mimeType, CancellationToken cancellationToken)
    {
        var photo = await codec.DecodeAsync(
            original,
            mimeType,
            MaximumFixedDimension,
            MaximumFixedDimension,
            PhotoScaling.ShrinkToFit,
            cancellationToken);
        PhotoToneNormalizer.Normalize(photo);
        return photo;
    }

    private async Task<PhotoPixels> LevelAndCropAsync(byte[] original, string mimeType, CancellationToken cancellationToken)
    {
        var photo = await codec.DecodeAsync(
            original,
            mimeType,
            MaximumFixedDimension,
            MaximumFixedDimension,
            PhotoScaling.ShrinkToFit,
            cancellationToken);
        var result = PhotoStraightener.TryStraightenAndCrop(photo, cancellationToken);
        return result?.Photo
            ?? throw new PhotoFixRejectedException(
                "This photo already appears level, or it does not contain enough clear horizontal or vertical lines to straighten safely.");
    }

    private async Task<PhotoPixels> RemoveClutterAsync(byte[] original, string mimeType, CancellationToken cancellationToken)
    {
        if (!clutterRemover.IsEnabled)
        {
            throw new PhotoFixRejectedException("Remove clutter is not switched on for this workspace.");
        }

        // A small preview gives the upright photo's shape, after any camera rotation, without decoding it in full.
        var preview = await codec.DecodeAsync(original, mimeType, 256, 256, PhotoScaling.ShrinkToFit, cancellationToken);
        var (canvasWidth, canvasHeight) = PhotoEditCanvas.SizeFor(preview.Width, preview.Height);
        var fitted = await codec.DecodeAsync(original, mimeType, canvasWidth, canvasHeight, PhotoScaling.Fit, cancellationToken);
        var canvas = PhotoEditCanvas.Pad(fitted, canvasWidth, canvasHeight, out var region);
        var edited = await clutterRemover.RemoveClutterAsync(
            new PhotoClutterRemovalRequest(
                await codec.EncodePngAsync(canvas, cancellationToken),
                await codec.EncodeMaskPngAsync(canvasWidth, canvasHeight, region, cancellationToken),
                canvasWidth,
                canvasHeight),
            cancellationToken);
        var result = await codec.DecodeAsync(edited, "image/png", canvasWidth, canvasHeight, PhotoScaling.Exact, cancellationToken);

        // The model redraws the whole frame, so only the areas it actually tidied are taken from it; the rest of
        // the photo stays the original, at its full resolution.
        var photo = await codec.DecodeAsync(
            original,
            mimeType,
            MaximumFixedDimension,
            MaximumFixedDimension,
            PhotoScaling.ShrinkToFit,
            cancellationToken);
        return PhotoEditMerger.Merge(photo, PhotoEditCanvas.Crop(result, region)).Photo;
    }

    private async Task<string?> ApplyAsync(
        Claim claim,
        string storedPath,
        long fileSize,
        PhotoPixels fixedPhoto,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var fix = await dbContext.PropertyPhotoFixes.SingleOrDefaultAsync(
            candidate => candidate.Id == claim.FixId && candidate.OrganizationId == claim.OrganizationId,
            cancellationToken);
        var media = await dbContext.PropertyMedia.SingleOrDefaultAsync(
            candidate => candidate.Id == claim.MediaId && candidate.OrganizationId == claim.OrganizationId,
            cancellationToken);
        if (fix is null
            || fix.Status != PropertyPhotoFixStatus.Processing
            || fix.AttemptCount != claim.Attempt
            || media is null
            || media.BlobPath != claim.SourceBlobPath)
        {
            throw new PhotoChangedException();
        }

        var now = timeProvider.GetUtcNow();
        var superseded = media.ApplyFix(
            claim.Kind,
            storedPath,
            "image/jpeg",
            fileSize,
            fixedPhoto.Width,
            fixedPhoto.Height,
            now);
        fix.Complete(now);
        await NotifyIfPropertyFinishedAsync(fix, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return superseded;
    }

    /// <summary>
    /// When this was the property's last fix in progress, tells the members how the fixes requested since the last
    /// photo-fix note went, so fixing several photos in a row gives one note rather than one per photo.
    /// </summary>
    private async Task NotifyIfPropertyFinishedAsync(PropertyPhotoFix finished, CancellationToken cancellationToken)
    {
        // Fixes finishing at the same moment take turns, so exactly one of them sees that none are left.
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT 1 FROM "Properties"
            WHERE "Id" = {finished.PropertyId} AND "OrganizationId" = {finished.OrganizationId}
            FOR UPDATE
            """, cancellationToken);
        var others = await dbContext.PropertyPhotoFixes.AsNoTracking()
            .Where(fix => fix.OrganizationId == finished.OrganizationId
                && fix.PropertyId == finished.PropertyId
                && fix.Id != finished.Id)
            .ToListAsync(cancellationToken);
        if (others.Any(fix => fix.Status is PropertyPhotoFixStatus.Pending or PropertyPhotoFixStatus.Processing
            || (fix.Status == PropertyPhotoFixStatus.Failed && fix.NextAttemptAtUtc is not null)))
        {
            return;
        }

        var lastNote = await dbContext.UserNotifications.AsNoTracking()
            .Where(notification => notification.OrganizationId == finished.OrganizationId
                && notification.PropertyId == finished.PropertyId
                && notification.Kind == UserNotificationKind.PhotoFixes)
            .MaxAsync(notification => (DateTimeOffset?)notification.CreatedAtUtc, cancellationToken);
        var batch = others.Append(finished)
            .Where(fix => fix.Id == finished.Id || lastNote is null || fix.RequestedAtUtc > lastNote)
            .ToArray();
        var completed = batch.Count(fix => fix.Status == PropertyPhotoFixStatus.Completed);
        var failed = batch.Count(fix => fix.Status == PropertyPhotoFixStatus.Failed);
        var property = await JobNotifications.GetPropertyLabelAsync(
            dbContext, finished.OrganizationId, finished.PropertyId, cancellationToken);
        await JobNotifications.AddAsync(
            dbContext,
            finished.OrganizationId,
            finished.PropertyId,
            null,
            JobNotificationText.PhotoFixesFinished(property, completed, failed, finished.LastError),
            timeProvider.GetUtcNow(),
            cancellationToken);
    }

    // The fixed photo is already saved; a clip refresh that loses a race is logged, and the next storyboard edit or
    // the Create walk-through clips button queues what is still missing.
    private async Task RequeueAffectedClipsAsync(Claim claim, CancellationToken cancellationToken)
    {
        try
        {
            await walkthroughTransitions.RequeueAffectedForOrganizationAsync(
                claim.OrganizationId,
                claim.PropertyId,
                cancellationToken);
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            dbContext.ChangeTracker.Clear();
            LogClipRefreshFailed(exception, claim.PropertyId);
        }
    }

    private async Task DeleteStoredAsync(string? storedPath)
    {
        if (storedPath is not null)
        {
            await storage.DeleteAsync(storedPath, CancellationToken.None);
        }
    }

    private static string SanitizeError(Exception exception) => exception switch
    {
        PhotoChangedException => "The photo changed while it was being fixed, so the fix was not applied.",
        FileNotFoundException => "The photo could not be read.",
        TimeoutException => "Fixing the photo took too long.",
        HttpRequestException => "The photo editing service could not be reached.",
        InvalidDataException => "The photo editing service returned an unusable photo.",
        PhotoFixRejectedException => exception.Message,
        _ => "Fixing the photo failed unexpectedly.",
    };

    private sealed record Claim(
        Guid FixId,
        Guid OrganizationId,
        Guid PropertyId,
        Guid MediaId,
        PhotoFix Kind,
        string SourceBlobPath,
        int Attempt);

    private sealed class PhotoChangedException() : Exception("The photo changed before its fix was applied.");

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Photo fix {FixId} ({Kind}) of photo {MediaId} failed on attempt {AttemptNumber}")]
    private partial void LogFixFailure(Exception exception, Guid fixId, Guid mediaId, PhotoFix kind, int attemptNumber);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Walk-through clips for property {PropertyId} were not requeued after a photo fix")]
    private partial void LogClipRefreshFailed(Exception exception, Guid propertyId);
}
