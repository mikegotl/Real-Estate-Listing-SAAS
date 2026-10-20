namespace ListingStudio.Domain.Properties;

public enum PropertyListingProvider
{
    Zillow,
}

/// <summary>
/// A tenant-owned reference to an external listing. The reference is intentionally only a link and audit record;
/// provider data and media are imported separately through an authorized provider adapter.
/// </summary>
public sealed class PropertyListingSource
{
    private PropertyListingSource()
    {
    }

    private PropertyListingSource(
        Guid organizationId,
        Guid propertyId,
        PropertyListingProvider provider,
        string sourceUrl,
        string externalListingId,
        string confirmedByUserId,
        DateTimeOffset now)
    {
        Id = Guid.NewGuid();
        OrganizationId = organizationId;
        PropertyId = propertyId;
        Provider = provider;
        CreatedAtUtc = now;
        Update(sourceUrl, externalListingId, confirmedByUserId, now);
    }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid PropertyId { get; private set; }
    public PropertyListingProvider Provider { get; private set; }
    public string SourceUrl { get; private set; } = string.Empty;
    public string ExternalListingId { get; private set; } = string.Empty;
    public string AddressConfirmedByUserId { get; private set; } = string.Empty;
    public DateTimeOffset AddressConfirmedAtUtc { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public ListingProperty Property { get; private set; } = null!;

    public static PropertyListingSource Create(
        Guid organizationId,
        Guid propertyId,
        PropertyListingProvider provider,
        string sourceUrl,
        string externalListingId,
        string confirmedByUserId,
        DateTimeOffset now)
    {
        if (organizationId == Guid.Empty || propertyId == Guid.Empty)
        {
            throw new ArgumentException("Organization and property are required.");
        }

        if (!Enum.IsDefined(provider))
        {
            throw new ArgumentOutOfRangeException(nameof(provider));
        }

        return new PropertyListingSource(
            organizationId,
            propertyId,
            provider,
            sourceUrl,
            externalListingId,
            confirmedByUserId,
            now);
    }

    public void Update(
        string sourceUrl,
        string externalListingId,
        string confirmedByUserId,
        DateTimeOffset now)
    {
        SourceUrl = Required(sourceUrl, 2_000, nameof(sourceUrl));
        ExternalListingId = Required(externalListingId, 100, nameof(externalListingId));
        AddressConfirmedByUserId = Required(confirmedByUserId, 450, nameof(confirmedByUserId));
        AddressConfirmedAtUtc = now;
        UpdatedAtUtc = now;
    }

    private static string Required(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        return normalized.Length <= maximumLength
            ? normalized
            : throw new ArgumentOutOfRangeException(parameterName, $"Value cannot exceed {maximumLength} characters.");
    }
}
