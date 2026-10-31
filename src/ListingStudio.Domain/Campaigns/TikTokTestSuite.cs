namespace ListingStudio.Domain.Campaigns;

public enum TikTokTestSuiteStatus { Queued, Running, Completed, Failed, Cancelled }

/// <summary>A bounded, explicitly requested three-campaign test run. Outputs remain ordinary private campaigns.</summary>
public sealed class TikTokTestSuite
{
    private TikTokTestSuite() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid PropertyId { get; private set; }
    public string RequestedByUserId { get; private set; } = string.Empty;
    public TikTokTestSuiteStatus Status { get; private set; }
    public int CurrentCaseIndex { get; private set; }
    public string? Error { get; private set; }
    public Guid? ShortVoiceoverJobId { get; private set; }
    public Guid? MusicOnlyJobId { get; private set; }
    public Guid? FullVoiceoverJobId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? ArchivedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public static TikTokTestSuite Create(Guid organizationId, Guid propertyId, string userId, DateTimeOffset now)
    {
        if (organizationId == Guid.Empty || propertyId == Guid.Empty)
            throw new ArgumentException("A workspace and property are required.");
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return new TikTokTestSuite { Id = Guid.NewGuid(), OrganizationId = organizationId, PropertyId = propertyId,
            RequestedByUserId = userId, Status = TikTokTestSuiteStatus.Queued, CreatedAtUtc = now, UpdatedAtUtc = now };
    }

    public Guid? CurrentJobId => CurrentCaseIndex switch
    {
        0 => ShortVoiceoverJobId, 1 => MusicOnlyJobId, 2 => FullVoiceoverJobId, _ => null,
    };

    public void AttachJob(Guid jobId, DateTimeOffset now)
    {
        if (jobId == Guid.Empty || Status is not (TikTokTestSuiteStatus.Queued or TikTokTestSuiteStatus.Running))
            throw new InvalidOperationException("The suite cannot attach this campaign.");
        switch (CurrentCaseIndex)
        {
            case 0: ShortVoiceoverJobId = jobId; break;
            case 1: MusicOnlyJobId = jobId; break;
            case 2: FullVoiceoverJobId = jobId; break;
            default: throw new InvalidOperationException("All three cases have already run.");
        }
        Status = TikTokTestSuiteStatus.Running;
        UpdatedAtUtc = now;
    }

    public void CompleteCase(DateTimeOffset now)
    {
        if (Status != TikTokTestSuiteStatus.Running || CurrentJobId is null)
            throw new InvalidOperationException("A running campaign is required.");
        CurrentCaseIndex++;
        Status = CurrentCaseIndex == 3 ? TikTokTestSuiteStatus.Completed : TikTokTestSuiteStatus.Queued;
        UpdatedAtUtc = now;
    }

    public void SetArchived(bool archived, DateTimeOffset now)
    {
        if (archived && (Status is TikTokTestSuiteStatus.Queued or TikTokTestSuiteStatus.Running))
            throw new InvalidOperationException("Cancel this active session before archiving it.");
        ArchivedAtUtc = archived ? ArchivedAtUtc ?? now : null;
        UpdatedAtUtc = now;
    }

    public void Stop(bool cancelled, string? error, DateTimeOffset now)
    {
        if (Status is TikTokTestSuiteStatus.Completed or TikTokTestSuiteStatus.Failed or TikTokTestSuiteStatus.Cancelled) return;
        Status = cancelled ? TikTokTestSuiteStatus.Cancelled : TikTokTestSuiteStatus.Failed;
        Error = error is { Length: > 500 } ? error[..500] : error;
        UpdatedAtUtc = now;
    }
}
