using ListingStudio.Domain.Organizations;
using ListingStudio.Domain.Properties;

namespace ListingStudio.Domain.Videos;

public sealed class VideoNarration
{
    private VideoNarration()
    {
    }

    private VideoNarration(
        Guid id,
        Guid organizationId,
        Guid propertyId,
        Guid videoProductionPlanId,
        int version,
        string generationVersion,
        string sourceFingerprint,
        string assetPath,
        string contentType,
        int durationMs,
        string? timingJson)
    {
        Id = id;
        OrganizationId = organizationId;
        PropertyId = propertyId;
        VideoProductionPlanId = videoProductionPlanId;
        Version = version;
        GenerationVersion = Required(generationVersion, 300, nameof(generationVersion));
        SourceFingerprint = Required(sourceFingerprint, 64, nameof(sourceFingerprint));
        AssetPath = Required(assetPath, 1_024, nameof(assetPath));
        ContentType = Required(contentType, 100, nameof(contentType));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(durationMs);
        DurationMs = durationMs;
        TimingJson = Optional(timingJson, 1_000_000, nameof(timingJson));
        CreatedAtUtc = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid PropertyId { get; private set; }

    public Guid VideoProductionPlanId { get; private set; }

    public int Version { get; private set; }

    public string GenerationVersion { get; private set; } = string.Empty;

    public string SourceFingerprint { get; private set; } = string.Empty;

    public string AssetPath { get; private set; } = string.Empty;

    public string ContentType { get; private set; } = string.Empty;

    public int DurationMs { get; private set; }

    public string? TimingJson { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public Organization Organization { get; private set; } = null!;

    public ListingProperty Property { get; private set; } = null!;

    public VideoProductionPlan VideoProductionPlan { get; private set; } = null!;

    public static VideoNarration Create(
        Guid id,
        Guid organizationId,
        Guid propertyId,
        Guid videoProductionPlanId,
        int version,
        string generationVersion,
        string sourceFingerprint,
        string assetPath,
        string contentType,
        int durationMs,
        string? timingJson)
    {
        if (id == Guid.Empty || organizationId == Guid.Empty || propertyId == Guid.Empty
            || videoProductionPlanId == Guid.Empty)
        {
            throw new ArgumentException("Narration, organization, property, and production plan identities are required.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        return new VideoNarration(
            id,
            organizationId,
            propertyId,
            videoProductionPlanId,
            version,
            generationVersion,
            sourceFingerprint,
            assetPath,
            contentType,
            durationMs,
            timingJson);
    }

    private static string Required(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return normalized;
    }

    private static string? Optional(string? value, int maximumLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Required(value, maximumLength, parameterName);
    }
}
