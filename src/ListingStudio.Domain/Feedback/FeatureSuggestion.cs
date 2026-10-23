using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Organizations;
using ListingStudio.Domain.Properties;

namespace ListingStudio.Domain.Feedback;

public sealed class FeatureSuggestion
{
    public const int MaximumCommentLength = 2_000;
    public const int MaximumPagePathLength = 300;
    public const int MaximumAdminEmailAttempts = 8;

    private FeatureSuggestion()
    {
    }

    private FeatureSuggestion(
        Guid organizationId,
        Guid? propertyId,
        string submittedByUserId,
        string comment,
        bool requiredForActiveCampaign,
        Guid? campaignGenerationJobId,
        string? pagePath,
        DateTimeOffset submittedAtUtc)
    {
        if (organizationId == Guid.Empty || propertyId == Guid.Empty)
        {
            throw new ArgumentException("An organization identity is required, and a linked property must have one.");
        }

        if (campaignGenerationJobId.HasValue && propertyId is null)
        {
            throw new ArgumentException("A campaign suggestion must be linked to the campaign's property.", nameof(propertyId));
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
        PagePath = Optional(pagePath, MaximumPagePathLength);
        SubmittedAtUtc = submittedAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    /// <summary>The property the comment was written about; null for a comment sent from a page outside a property.</summary>
    public Guid? PropertyId { get; private set; }

    public string SubmittedByUserId { get; private set; } = string.Empty;

    public string Comment { get; private set; } = string.Empty;

    public bool RequiredForActiveCampaign { get; private set; }

    public Guid? CampaignGenerationJobId { get; private set; }

    /// <summary>The app page the comment was sent from, such as /properties/{id} or /billing.</summary>
    public string? PagePath { get; private set; }

    public DateTimeOffset SubmittedAtUtc { get; private set; }

    /// <summary>When the comment was emailed to the platform admins; null while it is still waiting.</summary>
    public DateTimeOffset? AdminEmailSentAtUtc { get; private set; }

    public int AdminEmailAttemptCount { get; private set; }

    /// <summary>Earliest time the email is tried again after a failure, or after finding no admin to send it to.</summary>
    public DateTimeOffset? AdminEmailNextAttemptAtUtc { get; private set; }

    public Organization Organization { get; private set; } = null!;

    public ListingProperty? Property { get; private set; }

    public CampaignGenerationJob? CampaignGenerationJob { get; private set; }

    public static FeatureSuggestion Create(
        Guid organizationId,
        Guid? propertyId,
        string submittedByUserId,
        string comment,
        bool requiredForActiveCampaign,
        Guid? campaignGenerationJobId,
        DateTimeOffset submittedAtUtc,
        string? pagePath = null) =>
        new(
            organizationId,
            propertyId,
            submittedByUserId,
            comment,
            requiredForActiveCampaign,
            campaignGenerationJobId,
            pagePath,
            submittedAtUtc);

    public bool AdminEmailPending =>
        AdminEmailSentAtUtc is null && AdminEmailAttemptCount < MaximumAdminEmailAttempts;

    public void MarkAdminEmailSent(DateTimeOffset sentAtUtc)
    {
        AdminEmailSentAtUtc = sentAtUtc;
        AdminEmailAttemptCount++;
        AdminEmailNextAttemptAtUtc = null;
    }

    /// <summary>Records a failed send and backs off 1, 2, 4... minutes, capped at an hour. Returns whether it will be tried again.</summary>
    public bool RecordAdminEmailFailure(DateTimeOffset failedAtUtc)
    {
        AdminEmailAttemptCount++;
        var delayMinutes = Math.Min(60, 1 << Math.Min(AdminEmailAttemptCount - 1, 6));
        AdminEmailNextAttemptAtUtc = failedAtUtc.AddMinutes(delayMinutes);
        return AdminEmailPending;
    }

    /// <summary>No platform admin has an email address yet; check again later without using up an attempt.</summary>
    public void DeferAdminEmail(DateTimeOffset nextAttemptAtUtc) => AdminEmailNextAttemptAtUtc = nextAttemptAtUtc;

    private static string Required(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var trimmed = value.Trim();
        return trimmed.Length <= maximumLength
            ? trimmed
            : throw new ArgumentOutOfRangeException(parameterName);
    }

    private static string? Optional(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maximumLength ? trimmed : trimmed[..maximumLength];
    }
}
