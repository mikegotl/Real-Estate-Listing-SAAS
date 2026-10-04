using ListingStudio.Domain.Organizations;
using ListingStudio.Domain.Properties;
using ListingStudio.Domain.Videos;

namespace ListingStudio.Domain.Campaigns;

public enum CampaignGenerationStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled,
}

public enum CampaignGenerationStage
{
    ValidateProperty,
    AnalyzeMedia,
    GenerateStory,
    GenerateMasterVideoPlan,
    GenerateNarration,
    GenerateDerivativePlans,
    GenerateRequiredAiVideo,
    RenderHero,
    RenderFeature,
    RenderTeaser,
    GenerateSocialCopy,
    FinalizeCampaign,
}

public sealed class CampaignGenerationJob
{
    private CampaignGenerationJob()
    {
    }

    private CampaignGenerationJob(
        Guid organizationId,
        Guid propertyId,
        string requestedByUserId,
        string sourceFingerprint,
        DateTimeOffset now)
    {
        Id = Guid.NewGuid();
        OrganizationId = organizationId;
        PropertyId = propertyId;
        RequestedByUserId = Required(requestedByUserId, 450, nameof(requestedByUserId));
        SourceFingerprint = Required(sourceFingerprint, 64, nameof(sourceFingerprint));
        Status = CampaignGenerationStatus.Queued;
        CurrentStage = CampaignGenerationStage.ValidateProperty;
        NextAttemptAtUtc = now;
        CreatedAtUtc = now;
        UpdatedAtUtc = now;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid PropertyId { get; private set; }

    public string RequestedByUserId { get; private set; } = string.Empty;

    public string SourceFingerprint { get; private set; } = string.Empty;

    public CampaignGenerationStatus Status { get; private set; }

    public CampaignGenerationStage CurrentStage { get; private set; }

    public int StageAttemptCount { get; private set; }

    public int TotalStageAttempts { get; private set; }

    public DateTimeOffset? NextAttemptAtUtc { get; private set; }

    public DateTimeOffset? LeaseExpiresAtUtc { get; private set; }

    public bool CancellationRequested { get; private set; }

    public string? LastError { get; private set; }

    public Guid? MasterVideoProductionPlanId { get; private set; }

    public Guid? VideoNarrationId { get; private set; }

    public string? SocialCaptionLong { get; private set; }

    public string? SocialCaptionShort { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public Organization Organization { get; private set; } = null!;

    public ListingProperty Property { get; private set; } = null!;

    public VideoProductionPlan? MasterVideoProductionPlan { get; private set; }

    public VideoNarration? VideoNarration { get; private set; }

    public IReadOnlyCollection<CampaignDeliverable> Deliverables => deliverables;

    private readonly List<CampaignDeliverable> deliverables = [];

    public static CampaignGenerationJob Create(
        Guid organizationId,
        Guid propertyId,
        string requestedByUserId,
        string sourceFingerprint,
        DateTimeOffset now)
    {
        if (organizationId == Guid.Empty || propertyId == Guid.Empty)
        {
            throw new ArgumentException("Organization and property identities are required.");
        }

        return new CampaignGenerationJob(
            organizationId,
            propertyId,
            requestedByUserId,
            sourceFingerprint,
            now);
    }

    public void BeginStage(DateTimeOffset now, TimeSpan leaseDuration)
    {
        if (Status is not (CampaignGenerationStatus.Queued or CampaignGenerationStatus.Running)
            || CancellationRequested)
        {
            throw new InvalidOperationException("The campaign job cannot begin a stage.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);

        Status = CampaignGenerationStatus.Running;
        StageAttemptCount++;
        TotalStageAttempts++;
        LeaseExpiresAtUtc = now.Add(leaseDuration);
        NextAttemptAtUtc = null;
        LastError = null;
        UpdatedAtUtc = now;
    }

    public void CompleteStage(CampaignGenerationStage nextStage, DateTimeOffset now)
    {
        if (Status != CampaignGenerationStatus.Running || nextStage <= CurrentStage)
        {
            throw new InvalidOperationException("The campaign stage cannot be completed.");
        }

        CurrentStage = nextStage;
        Status = CampaignGenerationStatus.Queued;
        StageAttemptCount = 0;
        NextAttemptAtUtc = now;
        LeaseExpiresAtUtc = null;
        LastError = null;
        UpdatedAtUtc = now;
    }

    public void ContinueStage(DateTimeOffset now, TimeSpan delay)
    {
        if (Status != CampaignGenerationStatus.Running || delay < TimeSpan.Zero)
        {
            throw new InvalidOperationException("The campaign stage cannot be continued.");
        }

        Status = CampaignGenerationStatus.Queued;
        StageAttemptCount = 0;
        NextAttemptAtUtc = now.Add(delay);
        LeaseExpiresAtUtc = null;
        UpdatedAtUtc = now;
    }

    public void ScheduleRetry(string error, DateTimeOffset now, TimeSpan delay)
    {
        if (Status != CampaignGenerationStatus.Running || delay <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("The campaign stage cannot schedule a retry.");
        }

        Status = CampaignGenerationStatus.Queued;
        NextAttemptAtUtc = now.Add(delay);
        LeaseExpiresAtUtc = null;
        LastError = Required(error, 1_000, nameof(error));
        UpdatedAtUtc = now;
    }

    public void Fail(string error, DateTimeOffset now)
    {
        Status = CampaignGenerationStatus.Failed;
        NextAttemptAtUtc = null;
        LeaseExpiresAtUtc = null;
        LastError = Required(error, 1_000, nameof(error));
        UpdatedAtUtc = now;
    }

    public void Retry(DateTimeOffset now)
    {
        if (Status != CampaignGenerationStatus.Failed)
        {
            throw new InvalidOperationException("Only a failed campaign can be retried.");
        }

        Status = CampaignGenerationStatus.Queued;
        StageAttemptCount = 0;
        NextAttemptAtUtc = now;
        LeaseExpiresAtUtc = null;
        LastError = null;
        UpdatedAtUtc = now;
    }

    public void RequestCancellation(DateTimeOffset now)
    {
        if (Status is CampaignGenerationStatus.Completed or CampaignGenerationStatus.Cancelled)
        {
            return;
        }

        CancellationRequested = true;
        UpdatedAtUtc = now;
        if (Status is CampaignGenerationStatus.Queued or CampaignGenerationStatus.Failed)
        {
            Cancel(now);
        }
    }

    public void Cancel(DateTimeOffset now)
    {
        Status = CampaignGenerationStatus.Cancelled;
        NextAttemptAtUtc = null;
        LeaseExpiresAtUtc = null;
        LastError = null;
        UpdatedAtUtc = now;
        CompletedAtUtc = now;
    }

    public void SetMasterPlan(Guid planId)
    {
        if (planId == Guid.Empty)
        {
            throw new ArgumentException("A master plan identity is required.", nameof(planId));
        }

        MasterVideoProductionPlanId = planId;
    }

    public void SetNarration(Guid narrationId)
    {
        if (narrationId == Guid.Empty)
        {
            throw new ArgumentException("A narration identity is required.", nameof(narrationId));
        }

        VideoNarrationId = narrationId;
    }

    public void SetSocialCopy(string longCaption, string shortCaption)
    {
        SocialCaptionLong = Required(longCaption, 4_000, nameof(longCaption));
        SocialCaptionShort = Required(shortCaption, 1_000, nameof(shortCaption));
    }

    public void Complete(DateTimeOffset now)
    {
        if (Status != CampaignGenerationStatus.Running
            || CurrentStage != CampaignGenerationStage.FinalizeCampaign)
        {
            throw new InvalidOperationException("The campaign cannot be finalized from its current stage.");
        }

        Status = CampaignGenerationStatus.Completed;
        StageAttemptCount = 0;
        NextAttemptAtUtc = null;
        LeaseExpiresAtUtc = null;
        LastError = null;
        UpdatedAtUtc = now;
        CompletedAtUtc = now;
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
