using ListingStudio.Domain.Organizations;

namespace ListingStudio.Domain.Properties;

public sealed class ListingProperty
{
    private ListingProperty()
    {
    }

    private ListingProperty(Guid organizationId, PropertyDetails details)
    {
        Id = Guid.NewGuid();
        OrganizationId = organizationId;
        CreatedAtUtc = DateTimeOffset.UtcNow;
        Apply(details);
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public string Address1 { get; private set; } = string.Empty;

    public string? Address2 { get; private set; }

    public string City { get; private set; } = string.Empty;

    public string State { get; private set; } = string.Empty;

    public string ZipCode { get; private set; } = string.Empty;

    public decimal ListingPrice { get; private set; }

    public int Bedrooms { get; private set; }

    public decimal Bathrooms { get; private set; }

    public int? SquareFeet { get; private set; }

    public decimal? LotSize { get; private set; }

    public int? YearBuilt { get; private set; }

    public PropertyType PropertyType { get; private set; }

    public string? Description { get; private set; }

    public ListingStatus ListingStatus { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public DateTimeOffset? ArchivedAtUtc { get; private set; }

    public bool IsArchived => ArchivedAtUtc.HasValue;

    public Organization Organization { get; private set; } = null!;

    public static ListingProperty Create(Guid organizationId, PropertyDetails details)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("An organization is required.", nameof(organizationId));
        }

        ArgumentNullException.ThrowIfNull(details);
        return new ListingProperty(organizationId, details);
    }

    public void Update(PropertyDetails details)
    {
        if (IsArchived)
        {
            throw new InvalidOperationException("Archived properties cannot be edited.");
        }

        ArgumentNullException.ThrowIfNull(details);
        Apply(details);
    }

    public void Archive()
    {
        ArchivedAtUtc ??= DateTimeOffset.UtcNow;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    private void Apply(PropertyDetails details)
    {
        Address1 = Required(details.Address1, 200, nameof(details.Address1));
        Address2 = Optional(details.Address2, 200, nameof(details.Address2));
        City = Required(details.City, 100, nameof(details.City));
        State = Required(details.State, 100, nameof(details.State));
        ZipCode = Required(details.ZipCode, 20, nameof(details.ZipCode));
        Description = Optional(details.Description, 4_000, nameof(details.Description));

        if (details.ListingPrice < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(details), "Listing price cannot be negative.");
        }

        if (details.Bedrooms < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(details), "Bedrooms cannot be negative.");
        }

        if (details.Bathrooms < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(details), "Bathrooms cannot be negative.");
        }

        if (details.SquareFeet is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(details), "Square feet must be positive when provided.");
        }

        if (details.LotSize is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(details), "Lot size must be positive when provided.");
        }

        if (details.YearBuilt is < 1600 || details.YearBuilt > 2200)
        {
            throw new ArgumentOutOfRangeException(nameof(details), "Year built must be between 1600 and 2200.");
        }

        if (!Enum.IsDefined(details.PropertyType))
        {
            throw new ArgumentOutOfRangeException(nameof(details), "A valid property type is required.");
        }

        if (!Enum.IsDefined(details.ListingStatus))
        {
            throw new ArgumentOutOfRangeException(nameof(details), "A valid listing status is required.");
        }

        ListingPrice = details.ListingPrice;
        Bedrooms = details.Bedrooms;
        Bathrooms = details.Bathrooms;
        SquareFeet = details.SquareFeet;
        LotSize = details.LotSize;
        YearBuilt = details.YearBuilt;
        PropertyType = details.PropertyType;
        ListingStatus = details.ListingStatus;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    private static string Required(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return CheckLength(value.Trim(), maximumLength, parameterName);
    }

    private static string? Optional(string? value, int maximumLength, string parameterName)
    {
        return string.IsNullOrWhiteSpace(value) ? null : CheckLength(value.Trim(), maximumLength, parameterName);
    }

    private static string CheckLength(string value, int maximumLength, string parameterName)
    {
        if (value.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"Value cannot exceed {maximumLength} characters.");
        }

        return value;
    }
}
