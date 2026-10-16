using ListingStudio.Domain.Organizations;

namespace ListingStudio.Domain.Properties;

public enum WalkthroughTransitionStatus
{
    Pending,
    Submitting,
    Generating,
    Completed,
    Failed,
}

/// <summary>
/// A generated walk-through clip that moves the camera from one storyboard shot to the next, built from the two
/// listing photos as its first and last frames.
/// </summary>
public sealed class WalkthroughTransition
{
    public const int MaximumAttempts = 3;
    public const int MaximumErrorLength = 500;

    private WalkthroughTransition()
    {
    }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid PropertyId { get; private set; }
    public Guid FromPropertyMediaId { get; private set; }
    public Guid ToPropertyMediaId { get; private set; }
    public string GenerationVersion { get; private set; } = string.Empty;
    public WalkthroughTransitionStatus Status { get; private set; }
    public int AttemptCount { get; private set; }
    public string? ProviderRequestId { get; private set; }
    public DateTimeOffset? LastAttemptedAtUtc { get; private set; }
    public DateTimeOffset? SubmittedAtUtc { get; private set; }
    public DateTimeOffset? NextCheckAtUtc { get; private set; }
    public string? LastError { get; private set; }
    public string? Provider { get; private set; }
    public string? Model { get; private set; }
    public string? AssetPath { get; private set; }
    public string? ContentType { get; private set; }
    public long? FileSize { get; private set; }
    public int? DurationMs { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public Organization Organization { get; private set; } = null!;
    public ListingProperty Property { get; private set; } = null!;

    public bool CanRetry => Status == WalkthroughTransitionStatus.Failed && AttemptCount < MaximumAttempts;

    public static WalkthroughTransition Create(
        Guid organizationId,
        Guid propertyId,
        Guid fromPropertyMediaId,
        Guid toPropertyMediaId,
        string generationVersion,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? notBeforeUtc = null)
    {
        if (organizationId == Guid.Empty
            || propertyId == Guid.Empty
            || fromPropertyMediaId == Guid.Empty
            || toPropertyMediaId == Guid.Empty)
        {
            throw new ArgumentException("Organization, property, and both shot identities are required.");
        }

        if (fromPropertyMediaId == toPropertyMediaId)
        {
            throw new ArgumentException("A walk-through transition needs two different shots.");
        }

        return new WalkthroughTransition
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            PropertyId = propertyId,
            FromPropertyMediaId = fromPropertyMediaId,
            ToPropertyMediaId = toPropertyMediaId,
            GenerationVersion = Required(generationVersion, 100, nameof(generationVersion)),
            Status = WalkthroughTransitionStatus.Pending,
            CreatedAtUtc = createdAtUtc,
            // A pending clip queued by a storyboard edit waits until then, so a burst of edits settles first.
            NextCheckAtUtc = notBeforeUtc,
        };
    }

    /// <summary>Claims the transition for a new provider submission.</summary>
    public void BeginSubmission(DateTimeOffset now)
    {
        if (Status is not (WalkthroughTransitionStatus.Pending
            or WalkthroughTransitionStatus.Failed
            or WalkthroughTransitionStatus.Submitting))
        {
            throw new InvalidOperationException("Only pending or failed walk-through transitions can be submitted.");
        }

        if (AttemptCount >= MaximumAttempts)
        {
            throw new InvalidOperationException("The walk-through transition has no attempts left.");
        }

        AttemptCount++;
        Status = WalkthroughTransitionStatus.Submitting;
        ProviderRequestId = null;
        SubmittedAtUtc = null;
        NextCheckAtUtc = null;
        LastAttemptedAtUtc = now;
        LastError = null;
    }

    public void MarkSubmitted(string providerRequestId, DateTimeOffset now, DateTimeOffset nextCheckAtUtc)
    {
        EnsureStatus(WalkthroughTransitionStatus.Submitting);
        ProviderRequestId = Required(providerRequestId, 200, nameof(providerRequestId));
        Status = WalkthroughTransitionStatus.Generating;
        SubmittedAtUtc = now;
        NextCheckAtUtc = nextCheckAtUtc;
    }

    /// <summary>Records a status check that found the clip still generating.</summary>
    public void ScheduleCheck(DateTimeOffset nextCheckAtUtc)
    {
        EnsureStatus(WalkthroughTransitionStatus.Generating);
        NextCheckAtUtc = nextCheckAtUtc;
    }

    public void Complete(
        string provider,
        string model,
        string assetPath,
        string contentType,
        long fileSize,
        int durationMs,
        DateTimeOffset completedAtUtc)
    {
        EnsureStatus(WalkthroughTransitionStatus.Generating);
        if (fileSize <= 0 || durationMs is < 1_000 or > 60_000)
        {
            throw new ArgumentOutOfRangeException(nameof(durationMs));
        }

        Provider = Required(provider, 100, nameof(provider));
        Model = Required(model, 200, nameof(model));
        AssetPath = Required(assetPath, 1_024, nameof(assetPath));
        ContentType = Required(contentType, 100, nameof(contentType));
        FileSize = fileSize;
        DurationMs = durationMs;
        Status = WalkthroughTransitionStatus.Completed;
        NextCheckAtUtc = null;
        LastError = null;
        CompletedAtUtc = completedAtUtc;
    }

    public void Fail(string error, DateTimeOffset? retryAtUtc)
    {
        if (Status is not (WalkthroughTransitionStatus.Submitting or WalkthroughTransitionStatus.Generating))
        {
            throw new InvalidOperationException("Only an in-progress walk-through transition can fail.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        var trimmed = error.Trim();
        LastError = trimmed.Length <= MaximumErrorLength ? trimmed : trimmed[..MaximumErrorLength];
        Status = WalkthroughTransitionStatus.Failed;
        NextCheckAtUtc = AttemptCount < MaximumAttempts ? retryAtUtc : null;
    }

    /// <summary>Gives a transition that used all of its attempts a fresh set, when the agent asks again.</summary>
    public void Requeue()
    {
        if (Status != WalkthroughTransitionStatus.Failed)
        {
            throw new InvalidOperationException("Only failed walk-through transitions can be requeued.");
        }

        AttemptCount = 0;
        Status = WalkthroughTransitionStatus.Pending;
        NextCheckAtUtc = null;
        ProviderRequestId = null;
    }

    private void EnsureStatus(WalkthroughTransitionStatus expected)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException($"The walk-through transition is {Status}, not {expected}.");
        }
    }

    private static string Required(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        return normalized.Length <= maximumLength
            ? normalized
            : throw new ArgumentOutOfRangeException(parameterName);
    }
}
