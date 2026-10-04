using ListingStudio.Domain.Organizations;
using ListingStudio.Domain.Properties;

namespace ListingStudio.Domain.Stories;

public sealed class PropertyStory
{
    private string[] highlights = [];

    private PropertyStory()
    {
    }

    private PropertyStory(
        Guid organizationId,
        Guid propertyId,
        int version,
        string generationVersion,
        string sourceFingerprint,
        PropertyStoryContent content)
    {
        Id = Guid.NewGuid();
        OrganizationId = organizationId;
        PropertyId = propertyId;
        Version = version;
        GenerationVersion = Required(generationVersion, 100, nameof(generationVersion));
        SourceFingerprint = Required(sourceFingerprint, 64, nameof(sourceFingerprint));
        CampaignTitle = Required(content.CampaignTitle, 200, nameof(content.CampaignTitle));
        OpeningHook = Required(content.OpeningHook, 500, nameof(content.OpeningHook));
        PropertyNarrative = Required(content.PropertyNarrative, 4_000, nameof(content.PropertyNarrative));
        highlights = NormalizeHighlights(content.Highlights);
        VoiceoverScript = Required(content.VoiceoverScript, 6_000, nameof(content.VoiceoverScript));
        ClosingCta = Required(content.ClosingCta, 500, nameof(content.ClosingCta));
        SocialCaptionLong = Required(content.SocialCaptionLong, 2_200, nameof(content.SocialCaptionLong));
        SocialCaptionShort = Required(content.SocialCaptionShort, 500, nameof(content.SocialCaptionShort));
        CreatedAtUtc = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid PropertyId { get; private set; }

    public int Version { get; private set; }

    public string GenerationVersion { get; private set; } = string.Empty;

    public string SourceFingerprint { get; private set; } = string.Empty;

    public string CampaignTitle { get; private set; } = string.Empty;

    public string OpeningHook { get; private set; } = string.Empty;

    public string PropertyNarrative { get; private set; } = string.Empty;

    public IReadOnlyList<string> Highlights => highlights;

    public string VoiceoverScript { get; private set; } = string.Empty;

    public string ClosingCta { get; private set; } = string.Empty;

    public string SocialCaptionLong { get; private set; } = string.Empty;

    public string SocialCaptionShort { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public Organization Organization { get; private set; } = null!;

    public ListingProperty Property { get; private set; } = null!;

    public static PropertyStory Create(
        Guid organizationId,
        Guid propertyId,
        int version,
        string generationVersion,
        string sourceFingerprint,
        PropertyStoryContent content)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("An organization is required.", nameof(organizationId));
        }

        if (propertyId == Guid.Empty)
        {
            throw new ArgumentException("A property is required.", nameof(propertyId));
        }

        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "A positive story version is required.");
        }

        ArgumentNullException.ThrowIfNull(content);
        return new PropertyStory(
            organizationId,
            propertyId,
            version,
            generationVersion,
            sourceFingerprint,
            content);
    }

    public PropertyStoryContent GetContent() => new(
        CampaignTitle,
        OpeningHook,
        PropertyNarrative,
        highlights,
        VoiceoverScript,
        ClosingCta,
        SocialCaptionLong,
        SocialCaptionShort);

    private static string[] NormalizeHighlights(IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count is < 1 or > 12)
        {
            throw new ArgumentException("A story must contain between 1 and 12 highlights.", nameof(values));
        }

        return values.Select(value => Required(value, 500, nameof(values))).ToArray();
    }

    private static string Required(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"Value cannot exceed {maximumLength} characters.");
        }

        return normalized;
    }
}
