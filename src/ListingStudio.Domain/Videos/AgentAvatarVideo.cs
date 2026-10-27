using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Organizations;
using ListingStudio.Domain.Properties;

namespace ListingStudio.Domain.Videos;

public sealed class AgentAvatarVideo
{
    private AgentAvatarVideo()
    {
    }

    private AgentAvatarVideo(
        Guid organizationId,
        Guid propertyId,
        Guid agentPortraitId,
        Guid videoNarrationId,
        string sourceFingerprint,
        int introTimelineStartMs,
        int introDurationMs,
        int outroTimelineStartMs,
        int outroDurationMs,
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
        AgentPortraitId = agentPortraitId;
        VideoNarrationId = videoNarrationId;
        SourceFingerprint = Required(sourceFingerprint, 64, nameof(sourceFingerprint));
        IntroTimelineStartMs = introTimelineStartMs;
        IntroDurationMs = introDurationMs;
        OutroTimelineStartMs = outroTimelineStartMs;
        OutroDurationMs = outroDurationMs;
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
    public Guid AgentPortraitId { get; private set; }
    public Guid VideoNarrationId { get; private set; }
    public string SourceFingerprint { get; private set; } = string.Empty;
    public int IntroTimelineStartMs { get; private set; }
    public int IntroDurationMs { get; private set; }
    public int OutroTimelineStartMs { get; private set; }
    public int OutroDurationMs { get; private set; }
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
    public AgentPortrait AgentPortrait { get; private set; } = null!;
    public VideoNarration VideoNarration { get; private set; } = null!;

    public static AgentAvatarVideo Create(
        Guid organizationId,
        Guid propertyId,
        Guid agentPortraitId,
        Guid videoNarrationId,
        string sourceFingerprint,
        int introTimelineStartMs,
        int introDurationMs,
        int outroTimelineStartMs,
        int outroDurationMs,
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
        if (organizationId == Guid.Empty || propertyId == Guid.Empty || agentPortraitId == Guid.Empty
            || videoNarrationId == Guid.Empty)
        {
            throw new ArgumentException("Organization, property, portrait, and narration identities are required.");
        }

        if (introTimelineStartMs < 0 || introDurationMs <= 0 || outroTimelineStartMs < 0
            || outroDurationMs < 0 || width <= 0 || height <= 0 || fileSize <= 0
            || estimatedCostUsd < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(introDurationMs));
        }

        return new AgentAvatarVideo(
            organizationId, propertyId, agentPortraitId, videoNarrationId, sourceFingerprint,
            introTimelineStartMs, introDurationMs, outroTimelineStartMs, outroDurationMs,
            width, height, provider, model, generationVersion, providerRequestId,
            providerMetadataJson, estimatedCostUsd, assetPath, contentType, fileSize, createdAtUtc);
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
