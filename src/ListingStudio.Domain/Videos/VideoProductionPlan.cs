using ListingStudio.Domain.Organizations;
using ListingStudio.Domain.Properties;
using ListingStudio.Domain.Stories;

namespace ListingStudio.Domain.Videos;

public sealed class VideoProductionPlan
{
    private VideoProductionPlan()
    {
    }

    private VideoProductionPlan(
        Guid organizationId,
        Guid propertyId,
        Guid propertyStoryId,
        int version,
        RequestedDuration requestedDuration,
        VideoAspectRatio aspectRatio,
        string schemaVersion,
        string directorVersion,
        string sourceFingerprint,
        string specificationJson)
    {
        Id = Guid.NewGuid();
        OrganizationId = organizationId;
        PropertyId = propertyId;
        PropertyStoryId = propertyStoryId;
        Version = version;
        RequestedDuration = requestedDuration;
        AspectRatio = aspectRatio;
        SchemaVersion = Required(schemaVersion, 20, nameof(schemaVersion));
        DirectorVersion = Required(directorVersion, 100, nameof(directorVersion));
        SourceFingerprint = Required(sourceFingerprint, 64, nameof(sourceFingerprint));
        SpecificationJson = Required(specificationJson, 500_000, nameof(specificationJson));
        CreatedAtUtc = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid PropertyId { get; private set; }

    public Guid PropertyStoryId { get; private set; }

    public int Version { get; private set; }

    public RequestedDuration RequestedDuration { get; private set; }

    public VideoAspectRatio AspectRatio { get; private set; }

    public string SchemaVersion { get; private set; } = string.Empty;

    public string DirectorVersion { get; private set; } = string.Empty;

    public string SourceFingerprint { get; private set; } = string.Empty;

    public string SpecificationJson { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public Organization Organization { get; private set; } = null!;

    public ListingProperty Property { get; private set; } = null!;

    public PropertyStory PropertyStory { get; private set; } = null!;

    public static VideoProductionPlan Create(
        Guid organizationId,
        Guid propertyId,
        Guid propertyStoryId,
        int version,
        RequestedDuration requestedDuration,
        VideoAspectRatio aspectRatio,
        string schemaVersion,
        string directorVersion,
        string sourceFingerprint,
        string specificationJson)
    {
        if (organizationId == Guid.Empty || propertyId == Guid.Empty || propertyStoryId == Guid.Empty)
        {
            throw new ArgumentException("Organization, property, and story identities are required.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);

        if ((int)requestedDuration is < 15 or > 900 || !Enum.IsDefined(aspectRatio))
        {
            throw new ArgumentOutOfRangeException(nameof(requestedDuration), "The requested output is not supported.");
        }

        return new VideoProductionPlan(
            organizationId,
            propertyId,
            propertyStoryId,
            version,
            requestedDuration,
            aspectRatio,
            schemaVersion,
            directorVersion,
            sourceFingerprint,
            specificationJson);
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
}
