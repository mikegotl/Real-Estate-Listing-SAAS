namespace ListingStudio.Domain.Properties;

/// <summary>The fixes an agent can apply to a listing photo. A photo records every fix applied since its upload.</summary>
[Flags]
public enum PhotoFix
{
    None = 0,

    /// <summary>Evens out exposure and removes a color cast. Runs locally, without an AI service.</summary>
    BrightnessAndColor = 1,

    /// <summary>An AI image edit that takes out movable clutter and leaves the home itself untouched.</summary>
    RemoveClutter = 2,

    /// <summary>Levels a tilted photo and crops away the empty rotation edges. Runs locally.</summary>
    LevelAndCrop = 4,
}

public enum PropertyPhotoFixStatus
{
    Pending,
    Processing,
    Completed,
    Failed,
}

/// <summary>
/// A request to fix one listing photo. The Worker makes the fixed version from the photo as it was when the fix was
/// requested; if the photo changes before then, the result is thrown away rather than applied to a different photo.
/// </summary>
public sealed class PropertyPhotoFix
{
    public const int MaximumAttempts = 3;
    public const int MaximumErrorLength = 500;

    private PropertyPhotoFix()
    {
    }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid PropertyId { get; private set; }
    public Guid PropertyMediaId { get; private set; }
    public PhotoFix Kind { get; private set; }
    public PropertyPhotoFixStatus Status { get; private set; }

    // The photo content the fix starts from.
    public string SourceBlobPath { get; private set; } = string.Empty;
    public int AttemptCount { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset RequestedAtUtc { get; private set; }
    public DateTimeOffset? LastAttemptedAtUtc { get; private set; }
    public DateTimeOffset? NextAttemptAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public bool IsInProgress => Status is PropertyPhotoFixStatus.Pending or PropertyPhotoFixStatus.Processing
        || (Status == PropertyPhotoFixStatus.Failed && NextAttemptAtUtc is not null);

    public static PropertyPhotoFix Create(
        Guid organizationId,
        Guid propertyId,
        Guid propertyMediaId,
        PhotoFix kind,
        string sourceBlobPath,
        DateTimeOffset requestedAtUtc)
    {
        if (organizationId == Guid.Empty || propertyId == Guid.Empty || propertyMediaId == Guid.Empty)
        {
            throw new ArgumentException("Organization, property, and photo identities are required.");
        }

        if (kind is not (PhotoFix.BrightnessAndColor or PhotoFix.RemoveClutter or PhotoFix.LevelAndCrop))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), "A photo fix request is for exactly one fix.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(sourceBlobPath);
        return new PropertyPhotoFix
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            PropertyId = propertyId,
            PropertyMediaId = propertyMediaId,
            Kind = kind,
            Status = PropertyPhotoFixStatus.Pending,
            SourceBlobPath = sourceBlobPath,
            RequestedAtUtc = requestedAtUtc,
        };
    }

    public void Begin(DateTimeOffset now)
    {
        if (Status is not (PropertyPhotoFixStatus.Pending or PropertyPhotoFixStatus.Failed or PropertyPhotoFixStatus.Processing))
        {
            throw new InvalidOperationException("Only pending or failed photo fixes can be started.");
        }

        if (AttemptCount >= MaximumAttempts)
        {
            throw new InvalidOperationException("The photo fix has no attempts left.");
        }

        AttemptCount++;
        Status = PropertyPhotoFixStatus.Processing;
        LastAttemptedAtUtc = now;
        NextAttemptAtUtc = null;
        LastError = null;
    }

    public void Complete(DateTimeOffset now)
    {
        EnsureProcessing();
        Status = PropertyPhotoFixStatus.Completed;
        CompletedAtUtc = now;
        NextAttemptAtUtc = null;
        LastError = null;
    }

    /// <summary>Records a failure. A null retry time ends the request; otherwise the Worker tries again then.</summary>
    public void Fail(string error, DateTimeOffset? nextAttemptAtUtc)
    {
        EnsureProcessing();
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        var normalized = error.Trim();
        LastError = normalized.Length <= MaximumErrorLength ? normalized : normalized[..MaximumErrorLength];
        Status = PropertyPhotoFixStatus.Failed;
        NextAttemptAtUtc = AttemptCount < MaximumAttempts ? nextAttemptAtUtc : null;
    }

    private void EnsureProcessing()
    {
        if (Status != PropertyPhotoFixStatus.Processing)
        {
            throw new InvalidOperationException("The photo fix is not in progress.");
        }
    }
}
