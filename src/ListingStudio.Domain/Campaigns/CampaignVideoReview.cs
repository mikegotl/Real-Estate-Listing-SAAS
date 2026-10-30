namespace ListingStudio.Domain.Campaigns;

public enum CampaignVideoReviewDecision
{
    Approved,
    ChangesRequested,
}

/// <summary>An append-only owner decision about one exact rendered video version.</summary>
public sealed class CampaignVideoReview
{
    private CampaignVideoReview() { }

    public Guid Id { get; private set; }
    public long Sequence { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid CampaignDeliverableId { get; private set; }
    public int VideoVersion { get; private set; }
    public CampaignVideoReviewDecision Decision { get; private set; }
    public string ReviewerUserId { get; private set; } = string.Empty;
    public string? Feedback { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static CampaignVideoReview Create(
        Guid organizationId, Guid deliverableId, int version,
        CampaignVideoReviewDecision decision, string reviewerUserId, string? feedback, DateTimeOffset now)
    {
        if (organizationId == Guid.Empty || deliverableId == Guid.Empty || version < 1 || !Enum.IsDefined(decision))
        {
            throw new ArgumentException("An organization, rendered video version and supported decision are required.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewerUserId);
        if (reviewerUserId.Length > 450)
        {
            throw new ArgumentException("The reviewer identity is invalid.", nameof(reviewerUserId));
        }
        var normalized = feedback?.Trim();
        if (normalized?.Length > 1_000)
        {
            throw new ArgumentException("Keep review notes under 1,000 characters.", nameof(feedback));
        }
        if (decision == CampaignVideoReviewDecision.ChangesRequested && string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Describe the changes you want before submitting the request.", nameof(feedback));
        }
        return new CampaignVideoReview
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, CampaignDeliverableId = deliverableId,
            VideoVersion = version, Decision = decision, ReviewerUserId = reviewerUserId,
            Feedback = string.IsNullOrWhiteSpace(normalized) ? null : normalized, CreatedAtUtc = now,
        };
    }
}
