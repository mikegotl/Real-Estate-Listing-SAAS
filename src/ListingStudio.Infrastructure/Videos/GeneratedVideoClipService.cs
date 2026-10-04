using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ListingStudio.Application.Audio;
using ListingStudio.Application.Properties;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Videos;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ListingStudio.Infrastructure.Videos;

public sealed class GeneratedVideoClipService(
    ApplicationDbContext dbContext,
    IPropertyMediaStorage propertyMediaStorage,
    ICampaignAssetStorage campaignAssetStorage,
    IAiVideoProvider provider,
    TimeProvider timeProvider) : IGeneratedVideoClipService
{
    private static readonly HashSet<string> AllowedMotionInstructions = new(StringComparer.OrdinalIgnoreCase)
    {
        "slow cinematic push forward",
        "slow cinematic pull back",
        "slow horizontal pan",
        "slow camera push while preserving the property image",
    };

    public bool IsEnabled => provider.IsEnabled;

    public async Task<GeneratedVideoClipResult?> GetOrCreateAsync(
        Guid organizationId,
        Guid propertyId,
        Guid propertyMediaId,
        string motionInstruction,
        int durationMs,
        VideoAspectRatio aspectRatio,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return null;
        }

        if (organizationId == Guid.Empty || propertyId == Guid.Empty || propertyMediaId == Guid.Empty)
        {
            throw new ArgumentException("Organization, property, and source-media identities are required.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(motionInstruction);
        var normalizedInstruction = motionInstruction.Trim();
        if (!AllowedMotionInstructions.Contains(normalizedInstruction)
            || durationMs is < 1_000 or > 60_000
            || !Enum.IsDefined(aspectRatio))
        {
            throw new ArgumentException("The AI video request is outside the supported motion contract.");
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var media = await dbContext.PropertyMedia.FromSqlInterpolated($"""
            SELECT *
            FROM "PropertyMedia"
            WHERE "Id" = {propertyMediaId}
              AND "PropertyId" = {propertyId}
              AND "OrganizationId" = {organizationId}
            FOR UPDATE
            """).AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (media is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var fingerprint = Fingerprint(
            media.Id,
            media.UploadedAt,
            provider.GenerationVersion,
            normalizedInstruction,
            durationMs,
            aspectRatio);
        var existing = await dbContext.GeneratedVideoClips.AsNoTracking().SingleOrDefaultAsync(
            clip => clip.OrganizationId == organizationId
                && clip.PropertyId == propertyId
                && clip.SourceFingerprint == fingerprint,
            cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return ToResult(existing, reused: true);
        }

        await using var image = await propertyMediaStorage.OpenReadAsync(media.BlobPath, cancellationToken)
            ?? throw new FileNotFoundException("The source property image could not be opened.");
        var generated = await provider.GenerateAsync(
            new AiVideoProviderRequest(
                media.Id,
                media.OriginalFilename,
                media.MimeType,
                image,
                normalizedInstruction,
                durationMs,
                aspectRatio,
                fingerprint),
            cancellationToken);
        await using var video = generated.Content;
        ValidateResult(generated, durationMs, aspectRatio);
        var assetPath = $"organizations/{organizationId:N}/properties/{propertyId:N}/ai-video/{fingerprint}.mp4";
        var storeAttempted = false;
        try
        {
            var measured = new CountingReadStream(video);
            storeAttempted = true;
            await campaignAssetStorage.StoreAsync(assetPath, measured, generated.ContentType, cancellationToken);
            var metadataJson = JsonSerializer.Serialize(
                generated.Metadata.OrderBy(item => item.Key, StringComparer.Ordinal)
                    .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal));
            var clip = GeneratedVideoClip.Create(
                organizationId,
                propertyId,
                propertyMediaId,
                fingerprint,
                normalizedInstruction,
                generated.DurationMs,
                aspectRatio,
                generated.Width,
                generated.Height,
                generated.Provider,
                generated.Model,
                provider.GenerationVersion,
                generated.ProviderRequestId,
                metadataJson,
                generated.EstimatedCostUsd,
                assetPath,
                generated.ContentType,
                measured.BytesRead,
                timeProvider.GetUtcNow());
            dbContext.GeneratedVideoClips.Add(clip);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ToResult(clip, reused: false);
        }
        catch
        {
            if (storeAttempted)
            {
                await campaignAssetStorage.DeleteAsync(assetPath, CancellationToken.None);
            }

            throw;
        }
    }

    private static void ValidateResult(
        AiVideoProviderResult result,
        int requestedDurationMs,
        VideoAspectRatio aspectRatio)
    {
        if (!string.Equals(result.ContentType, "video/mp4", StringComparison.OrdinalIgnoreCase)
            || result.Width <= 0
            || result.Height <= 0
            || result.DurationMs < requestedDurationMs
            || string.IsNullOrWhiteSpace(result.Provider)
            || string.IsNullOrWhiteSpace(result.Model)
            || result.EstimatedCostUsd < 0)
        {
            throw new InvalidDataException("The AI video provider returned invalid clip metadata.");
        }

        var actualRatio = result.Width / (decimal)result.Height;
        var expectedRatio = aspectRatio == VideoAspectRatio.Landscape16By9 ? 16m / 9m : 9m / 16m;
        if (Math.Abs(actualRatio - expectedRatio) > 0.02m)
        {
            throw new InvalidDataException("The AI video clip does not match the requested aspect ratio.");
        }

        if (result.Metadata.Count > 50
            || result.Metadata.Any(item => item.Key.Length > 100 || item.Value.Length > 500))
        {
            throw new InvalidDataException("The AI video provider metadata exceeds storage limits.");
        }
    }

    private static string Fingerprint(
        Guid mediaId,
        DateTimeOffset uploadedAt,
        string generationVersion,
        string instruction,
        int durationMs,
        VideoAspectRatio aspectRatio)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generationVersion);
        var source = JsonSerializer.Serialize(new
        {
            mediaId,
            uploadedAt,
            generationVersion,
            instruction,
            durationMs,
            aspectRatio,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
    }

    private static GeneratedVideoClipResult ToResult(GeneratedVideoClip clip, bool reused) => new(
        clip.Id,
        clip.PropertyMediaId,
        clip.DurationMs,
        clip.AspectRatio,
        clip.Width,
        clip.Height,
        clip.Provider,
        clip.Model,
        clip.EstimatedCostUsd,
        reused);

    private sealed class CountingReadStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            BytesRead += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
