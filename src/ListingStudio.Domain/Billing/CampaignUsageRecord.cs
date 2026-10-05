namespace ListingStudio.Domain.Billing;

using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Organizations;

public sealed class CampaignUsageRecord
{
    private CampaignUsageRecord()
    {
    }

    private CampaignUsageRecord(
        Guid organizationId,
        Guid campaignGenerationJobId,
        DateTimeOffset periodStartUtc,
        bool isAdditionalUsage,
        DateTimeOffset recordedAtUtc)
    {
        Id = Guid.NewGuid();
        OrganizationId = organizationId;
        CampaignGenerationJobId = campaignGenerationJobId;
        PeriodStartUtc = periodStartUtc;
        IsAdditionalUsage = isAdditionalUsage;
        RecordedAtUtc = recordedAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid CampaignGenerationJobId { get; private set; }

    public DateTimeOffset PeriodStartUtc { get; private set; }

    public bool IsAdditionalUsage { get; private set; }

    public DateTimeOffset RecordedAtUtc { get; private set; }

    public Organization Organization { get; private set; } = null!;

    public CampaignGenerationJob CampaignGenerationJob { get; private set; } = null!;

    public static CampaignUsageRecord Create(
        Guid organizationId,
        Guid campaignGenerationJobId,
        DateTimeOffset periodStartUtc,
        bool isAdditionalUsage,
        DateTimeOffset recordedAtUtc)
    {
        if (organizationId == Guid.Empty || campaignGenerationJobId == Guid.Empty)
        {
            throw new ArgumentException("Organization and campaign identities are required.");
        }

        return new CampaignUsageRecord(
            organizationId,
            campaignGenerationJobId,
            periodStartUtc,
            isAdditionalUsage,
            recordedAtUtc);
    }
}
