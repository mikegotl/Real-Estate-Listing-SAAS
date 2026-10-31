using ListingStudio.Domain.Organizations;

namespace ListingStudio.Domain.Properties;

public enum AerialIntroClipStatus
{
    Pending,
    Submitting,
    Generating,
    Completed,
    Failed,
}

/// <summary>
/// An optional AI "drone arrival" for the start of the tour: generated from the Exterior front photo as a rising
/// pull-back, then played backwards so it descends and lands exactly on that real photo. Everything above the photo is
/// invented by the model, so the stored clip carries a burned-in disclosure, the agent acknowledges that before
/// asking for it, and campaign videos use it only after the agent approves it. A property has at most one.
/// </summary>
public sealed class AerialIntroClip
{
    public const int MaximumAttempts = 3;
    public const int MaximumErrorLength = 500;
    public const int LowHeightFeet = 50;
    public const int HighHeightFeet = 100;
    public const decimal SmoothPlaybackRate = 1m;
    public const decimal BriskPlaybackRate = 1.5m;
    public const decimal FastPlaybackRate = 2m;

    private AerialIntroClip()
    {
    }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid PropertyId { get; private set; }

    /// <summary>The Exterior front photo the drone lands on.</summary>
    public Guid PropertyMediaId { get; private set; }

    /// <summary>The photo file the clip was made from, so fixing or replacing the photo makes the clip out of date.</summary>
    public string SourceBlobPath { get; private set; } = string.Empty;

    public int HeightFeet { get; private set; }
    public decimal PlaybackRate { get; private set; } = BriskPlaybackRate;
    public string GenerationVersion { get; private set; } = string.Empty;
    public AerialIntroClipStatus Status { get; private set; }
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
    public string RequestedByUserId { get; private set; } = string.Empty;

    /// <summary>When the agent confirmed the clip is AI-generated and may not match the real property.</summary>
    public DateTimeOffset DisclosureAcknowledgedAtUtc { get; private set; }

    public string? ApprovedByUserId { get; private set; }
    public DateTimeOffset? ApprovedAtUtc { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public Organization Organization { get; private set; } = null!;
    public ListingProperty Property { get; private set; } = null!;

    public bool IsApproved => ApprovedAtUtc is not null;

    public bool CanRetry => Status == AerialIntroClipStatus.Failed && AttemptCount < MaximumAttempts;

    public bool IsInProgress => Status is AerialIntroClipStatus.Pending
        or AerialIntroClipStatus.Submitting
        or AerialIntroClipStatus.Generating
        || CanRetry;

    public static bool IsSupportedHeight(int heightFeet) => heightFeet is LowHeightFeet or HighHeightFeet;

    public static bool IsSupportedPlaybackRate(decimal playbackRate) =>
        playbackRate is SmoothPlaybackRate or BriskPlaybackRate or FastPlaybackRate;

    public static AerialIntroClip Create(
        Guid organizationId,
        Guid propertyId,
        Guid propertyMediaId,
        string sourceBlobPath,
        int heightFeet,
        string generationVersion,
        string requestedByUserId,
        DateTimeOffset now)
    {
        if (organizationId == Guid.Empty || propertyId == Guid.Empty || propertyMediaId == Guid.Empty)
        {
            throw new ArgumentException("Organization, property, and photo identities are required.");
        }

        if (!IsSupportedHeight(heightFeet))
        {
            throw new ArgumentOutOfRangeException(nameof(heightFeet), "The drone height must be 50 or 100 feet.");
        }

        return new AerialIntroClip
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            PropertyId = propertyId,
            PropertyMediaId = propertyMediaId,
            SourceBlobPath = Required(sourceBlobPath, 1_024, nameof(sourceBlobPath)),
            HeightFeet = heightFeet,
            PlaybackRate = BriskPlaybackRate,
            GenerationVersion = Required(generationVersion, 100, nameof(generationVersion)),
            RequestedByUserId = Required(requestedByUserId, 450, nameof(requestedByUserId)),
            DisclosureAcknowledgedAtUtc = now,
            Status = AerialIntroClipStatus.Pending,
            CreatedAtUtc = now,
        };
    }

    /// <summary>Claims the clip for a new provider submission.</summary>
    public void BeginSubmission(DateTimeOffset now)
    {
        if (Status is not (AerialIntroClipStatus.Pending
            or AerialIntroClipStatus.Failed
            or AerialIntroClipStatus.Submitting))
        {
            throw new InvalidOperationException("Only pending or failed drone intros can be submitted.");
        }

        if (AttemptCount >= MaximumAttempts)
        {
            throw new InvalidOperationException("The drone intro has no attempts left.");
        }

        AttemptCount++;
        Status = AerialIntroClipStatus.Submitting;
        ProviderRequestId = null;
        SubmittedAtUtc = null;
        NextCheckAtUtc = null;
        LastAttemptedAtUtc = now;
        LastError = null;
    }

    public void MarkSubmitted(string providerRequestId, DateTimeOffset now, DateTimeOffset nextCheckAtUtc)
    {
        EnsureStatus(AerialIntroClipStatus.Submitting);
        ProviderRequestId = Required(providerRequestId, 200, nameof(providerRequestId));
        Status = AerialIntroClipStatus.Generating;
        SubmittedAtUtc = now;
        NextCheckAtUtc = nextCheckAtUtc;
    }

    /// <summary>Records a status check that found the clip still generating.</summary>
    public void ScheduleCheck(DateTimeOffset nextCheckAtUtc)
    {
        EnsureStatus(AerialIntroClipStatus.Generating);
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
        EnsureStatus(AerialIntroClipStatus.Generating);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fileSize);
        if (durationMs is < 1_000 or > 60_000)
        {
            throw new ArgumentOutOfRangeException(nameof(durationMs));
        }

        Provider = Required(provider, 100, nameof(provider));
        Model = Required(model, 200, nameof(model));
        AssetPath = Required(assetPath, 1_024, nameof(assetPath));
        ContentType = Required(contentType, 100, nameof(contentType));
        FileSize = fileSize;
        DurationMs = durationMs;
        Status = AerialIntroClipStatus.Completed;
        NextCheckAtUtc = null;
        LastError = null;
        CompletedAtUtc = completedAtUtc;
    }

    public void Fail(string error, DateTimeOffset? retryAtUtc)
    {
        if (Status is not (AerialIntroClipStatus.Submitting or AerialIntroClipStatus.Generating))
        {
            throw new InvalidOperationException("Only an in-progress drone intro can fail.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        var trimmed = error.Trim();
        LastError = trimmed.Length <= MaximumErrorLength ? trimmed : trimmed[..MaximumErrorLength];
        Status = AerialIntroClipStatus.Failed;
        NextCheckAtUtc = AttemptCount < MaximumAttempts ? retryAtUtc : null;
    }

    /// <summary>The agent watched the finished clip and allows campaign videos to open with it.</summary>
    public void Approve(string userId, DateTimeOffset now)
    {
        EnsureStatus(AerialIntroClipStatus.Completed);
        ApprovedByUserId = Required(userId, 450, nameof(userId));
        ApprovedAtUtc = now;
    }

    public void SetPlaybackRate(decimal playbackRate)
    {
        if (!IsSupportedPlaybackRate(playbackRate))
        {
            throw new ArgumentOutOfRangeException(nameof(playbackRate));
        }

        PlaybackRate = playbackRate;
    }

    private void EnsureStatus(AerialIntroClipStatus expected)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException($"The drone intro is {Status}, not {expected}.");
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
