using ListingStudio.Domain.Properties;

namespace ListingStudio.Domain.Leads;

public sealed class LeadCapturePage
{
    public const int MaximumHeadlineLength = 160;

    private LeadCapturePage()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid PropertyId { get; private set; }

    public string PublicSlug { get; private set; } = string.Empty;

    public string? Headline { get; private set; }

    public bool IsPublished { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public ListingProperty Property { get; private set; } = null!;

    public static LeadCapturePage Create(
        Guid organizationId,
        Guid propertyId,
        string publicSlug,
        DateTimeOffset now)
    {
        if (organizationId == Guid.Empty || propertyId == Guid.Empty)
        {
            throw new ArgumentException("An organization and property are required.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(publicSlug);
        var slug = publicSlug.Trim();
        if (slug.Length != 32 || slug.Any(character => !char.IsAsciiHexDigit(character)))
        {
            throw new ArgumentException("The public slug must be a 32-character hexadecimal value.", nameof(publicSlug));
        }

        return new LeadCapturePage
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            PropertyId = propertyId,
            PublicSlug = slug.ToLowerInvariant(),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
    }

    public void Update(string? headline, bool published, DateTimeOffset now)
    {
        Headline = string.IsNullOrWhiteSpace(headline)
            ? null
            : headline.Trim().Length <= MaximumHeadlineLength
                ? headline.Trim()
                : throw new ArgumentOutOfRangeException(nameof(headline), $"Headline cannot exceed {MaximumHeadlineLength} characters.");
        IsPublished = published;
        UpdatedAtUtc = now;
    }
}
