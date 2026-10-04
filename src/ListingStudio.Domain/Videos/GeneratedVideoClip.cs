using ListingStudio.Domain.Organizations;
using ListingStudio.Domain.Properties;

namespace ListingStudio.Domain.Videos;

public sealed class GeneratedVideoClip
{
    private GeneratedVideoClip()
    {
    }

    private GeneratedVideoClip(
        Guid organizationId,
        Guid propertyId,
        Guid propertyMediaId,
        string sourceFingerprint,
        string motionInstruction,
        int durationMs,
        VideoAspectRatio aspectRatio,
        int width,
        int height,
        string provider,
        string model,
        string generationVersion,
        string? providerRequestId,
        string providerMetadataJson,
        decimal? estimatedCostUsd,
        string assetPath,
        string contentType,
        long fileSize,
        DateTimeOffset createdAtUtc)
    {
        Id = Guid.NewGuid();
        OrganizationId = organizationId;
        PropertyId = propertyId;
        PropertyMediaId = propertyMediaId;
        SourceFingerprint = Required(sourceFingerprint, 64, nameof(sourceFingerprint));
        MotionInstruction = Required(motionInstruction, 200, nameof(motionInstruction));
        DurationMs = durationMs;
        AspectRatio = aspectRatio;
        Width = width;
        Height = height;
        Provider = Required(provider, 100, nameof(provider));
        Model = Required(model, 200, nameof(model));
        GenerationVersion = Required(generationVersion, 100, nameof(generationVersion));
        ProviderRequestId = Optional(providerRequestId, 200, nameof(providerRequestId));
        ProviderMetadataJson = Required(providerMetadataJson, 20_000, nameof(providerMetadataJson));
        EstimatedCostUsd = estimatedCostUsd;
        AssetPath = Required(assetPath, 1_024, nameof(assetPath));
        ContentType = Required(contentType, 100, nameof(contentType));
        FileSize = fileSize;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid PropertyId { get; private set; }
    public Guid PropertyMediaId { get; private set; }
    public string SourceFingerprint { get; private set; } = string.Empty;
    public string MotionInstruction { get; private set; } = string.Empty;
    public int DurationMs { get; private set; }
    public VideoAspectRatio AspectRatio { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public string Provider { get; private set; } = string.Empty;
    public string Model { get; private set; } = string.Empty;
    public string GenerationVersion { get; private set; } = string.Empty;
    public string? ProviderRequestId { get; private set; }
    public string ProviderMetadataJson { get; private set; } = string.Empty;
    public decimal? EstimatedCostUsd { get; private set; }
    public string AssetPath { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = string.Empty;
    public long FileSize { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }

    public Organization Organization { get; private set; } = null!;
    public ListingProperty Property { get; private set; } = null!;
    public PropertyMedia PropertyMedia { get; private set; } = null!;

    public static GeneratedVideoClip Create(
        Guid organizationId,
        Guid propertyId,
        Guid propertyMediaId,
        string sourceFingerprint,
        string motionInstruction,
        int durationMs,
        VideoAspectRatio aspectRatio,
        int width,
        int height,
        string provider,
        string model,
        string generationVersion,
        string? providerRequestId,
        string providerMetadataJson,
        decimal? estimatedCostUsd,
        string assetPath,
        string contentType,
        long fileSize,
        DateTimeOffset createdAtUtc)
    {
        if (organizationId == Guid.Empty || propertyId == Guid.Empty || propertyMediaId == Guid.Empty)
        {
            throw new ArgumentException("Organization, property, and source-media identities are required.");
        }

        if (durationMs is < 1_000 or > 60_000 || width <= 0 || height <= 0 || fileSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationMs));
        }

        if (!Enum.IsDefined(aspectRatio) || estimatedCostUsd < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(aspectRatio));
        }

        return new GeneratedVideoClip(
            organizationId, propertyId, propertyMediaId, sourceFingerprint, motionInstruction,
            durationMs, aspectRatio, width, height, provider, model, generationVersion,
            providerRequestId, providerMetadataJson, estimatedCostUsd, assetPath, contentType,
            fileSize, createdAtUtc);
    }

    private static string Required(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        return normalized.Length <= maximumLength
            ? normalized
            : throw new ArgumentOutOfRangeException(parameterName);
    }

    private static string? Optional(string? value, int maximumLength, string parameterName) =>
        string.IsNullOrWhiteSpace(value) ? null : Required(value, maximumLength, parameterName);
}
