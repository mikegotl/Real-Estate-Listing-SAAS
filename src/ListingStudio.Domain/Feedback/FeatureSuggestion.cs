using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Organizations;
using ListingStudio.Domain.Properties;

namespace ListingStudio.Domain.Feedback;

public sealed class FeatureSuggestion
{
    public const int MaximumCommentLength = 2_000;

    private FeatureSuggestion()
    {
    }

    private FeatureSuggestion(
        Guid organizationId,
        Guid propertyId,
        string submittedByUserId,
        string comment,
        bool requiredForActiveCampaign,
        Guid? campaignGenerationJobId,
        DateTimeOffset submittedAtUtc)
    {
        if (organizationId == Guid.Empty || propertyId == Guid.Empty)
        {
            throw new ArgumentException("Organization and property identities are required.");
        }

        if (requiredForActiveCampaign != campaignGenerationJobId.HasValue)
        {
            throw new ArgumentException(
                "A suggestion required for an active campaign must reference that campaign.",
                nameof(campaignGenerationJobId));
        }

        Id = Guid.NewGuid();
        OrganizationId = organizationId;
        PropertyId = propertyId;
        SubmittedByUserId = Required(submittedByUserId, 450, nameof(submittedByUserId));
        Comment = Required(comment, MaximumCommentLength, nameof(comment));
        RequiredForActiveCampaign = requiredForActiveCampaign;
        CampaignGenerationJobId = campaignGenerationJobId;
        SubmittedAtUtc = submittedAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid PropertyId { get; private set; }

    public string SubmittedByUserId { get; private set; } = string.Empty;

    public string Comment { get; private set; } = string.Empty;

    public bool RequiredForActiveCampaign { get; private set; }

    public Guid? CampaignGenerationJobId { get; private set; }

    public DateTimeOffset SubmittedAtUtc { get; private set; }

    public Organization Organization { get; private set; } = null!;

    public ListingProperty Property { get; private set; } = null!;

    public CampaignGenerationJob? CampaignGenerationJob { get; private set; }

    public static FeatureSuggestion Create(
        Guid organizationId,
        Guid propertyId,
        string submittedByUserId,
        string comment,
        bool requiredForActiveCampaign,
        Guid? campaignGenerationJobId,
        DateTimeOffset submittedAtUtc) =>
        new(
            organizationId,
            propertyId,
            submittedByUserId,
            comment,
            requiredForActiveCampaign,
            campaignGenerationJobId,
            submittedAtUtc);

    private static string Required(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var trimmed = value.Trim();
        return trimmed.Length <= maximumLength
            ? trimmed
            : throw new ArgumentOutOfRangeException(parameterName);
    }
}
