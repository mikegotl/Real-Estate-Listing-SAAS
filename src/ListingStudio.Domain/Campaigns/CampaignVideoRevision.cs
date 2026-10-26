namespace ListingStudio.Domain.Campaigns;

public enum CampaignVideoChatRole
{
    User,
    Assistant,
}

/// <summary>
/// One message in the chat beside a finished campaign video. An assistant message can carry proposed edits,
/// made against the video version it names, that the user can then render. Messages are kept permanently, apart
/// from the video, so platform administrators can review what users ask for.
/// </summary>
public sealed class CampaignVideoChatMessage
{
    public const int MaximumTextLength = 2_000;

    private CampaignVideoChatMessage()
    {
    }

    private CampaignVideoChatMessage(
        CampaignDeliverable deliverable,
        string? userId,
        CampaignVideoChatRole role,
        string text,
        string? editsJson,
        int videoVersion,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(deliverable);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(videoVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var normalized = text.Trim();
        if (normalized.Length > MaximumTextLength)
        {
            normalized = normalized[..MaximumTextLength];
        }

        if (editsJson is { Length: > 20_000 })
        {
            throw new ArgumentOutOfRangeException(nameof(editsJson));
        }

        Id = Guid.NewGuid();
        OrganizationId = deliverable.OrganizationId;
        PropertyId = deliverable.PropertyId;
        CampaignGenerationJobId = deliverable.CampaignGenerationJobId;
        CampaignDeliverableId = deliverable.Id;
        Kind = deliverable.Kind;
        UserId = userId;
        Role = role;
        Text = normalized;
        EditsJson = string.IsNullOrWhiteSpace(editsJson) ? null : editsJson;
        VideoVersion = videoVersion;
        CreatedAtUtc = now;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid PropertyId { get; private set; }

    public Guid CampaignGenerationJobId { get; private set; }

    public Guid CampaignDeliverableId { get; private set; }

    public CampaignOutputKind Kind { get; private set; }

    public string? UserId { get; private set; }

    public CampaignVideoChatRole Role { get; private set; }

    public string Text { get; private set; } = string.Empty;

    /// <summary>The proposed edits, already checked against the video, or null when nothing is proposed.</summary>
    public string? EditsJson { get; private set; }

    /// <summary>The video version the message was written against.</summary>
    public int VideoVersion { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static CampaignVideoChatMessage FromUser(
        CampaignDeliverable deliverable,
        string userId,
        string text,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return new CampaignVideoChatMessage(
            deliverable, userId, CampaignVideoChatRole.User, text, null, deliverable.Version, now);
    }

    public static CampaignVideoChatMessage FromAssistant(
        CampaignDeliverable deliverable,
        string text,
        string? editsJson,
        DateTimeOffset now) =>
        new(deliverable, null, CampaignVideoChatRole.Assistant, text, editsJson, deliverable.Version, now);
}

public enum CampaignVideoRevisionStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Undone,
}

/// <summary>
/// A re-render of one campaign video with the edits from a chat message. When it completes it keeps what the
/// video was before, so the change can be undone.
/// </summary>
public sealed class CampaignVideoRevision
{
    public const int MaximumAttempts = 3;

    /// <summary>Renders a user can request per video for free; failed renders do not count.</summary>
    public const int FreeRendersPerVideo = 5;

    private CampaignVideoRevision()
    {
    }

    private CampaignVideoRevision(
        Guid organizationId,
        Guid propertyId,
        Guid campaignGenerationJobId,
        Guid campaignDeliverableId,
        Guid chatMessageId,
        string requestedByUserId,
        int baseVersion,
        string editsJson,
        DateTimeOffset now)
    {
        Id = Guid.NewGuid();
        OrganizationId = organizationId;
        PropertyId = propertyId;
        CampaignGenerationJobId = campaignGenerationJobId;
        CampaignDeliverableId = campaignDeliverableId;
        ChatMessageId = chatMessageId;
        RequestedByUserId = requestedByUserId;
        BaseVersion = baseVersion;
        EditsJson = editsJson;
        Status = CampaignVideoRevisionStatus.Queued;
        NextAttemptAtUtc = now;
        CreatedAtUtc = now;
        UpdatedAtUtc = now;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid PropertyId { get; private set; }

    public Guid CampaignGenerationJobId { get; private set; }

    public Guid CampaignDeliverableId { get; private set; }

    public Guid ChatMessageId { get; private set; }

    public string RequestedByUserId { get; private set; } = string.Empty;

    /// <summary>The video version the edits apply to; the render fails if the video has moved on since.</summary>
    public int BaseVersion { get; private set; }

    /// <summary>The version this revision becomes once rendered.</summary>
    public int? ResultVersion { get; private set; }

    public string EditsJson { get; private set; } = string.Empty;

    public CampaignVideoRevisionStatus Status { get; private set; }

    public int AttemptCount { get; private set; }

    public DateTimeOffset? NextAttemptAtUtc { get; private set; }

    public DateTimeOffset? LeaseExpiresAtUtc { get; private set; }

    public string? LastError { get; private set; }

    public string? PreviousSpecificationJson { get; private set; }

    public Guid? PreviousVideoNarrationId { get; private set; }

    public string? PreviousAssetPath { get; private set; }

    public string? PreviousContentType { get; private set; }

    public long? PreviousFileSize { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public static CampaignVideoRevision Create(
        CampaignDeliverable deliverable,
        Guid chatMessageId,
        string requestedByUserId,
        string editsJson,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(deliverable);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedByUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(editsJson);
        if (chatMessageId == Guid.Empty)
        {
            throw new ArgumentException("A chat message is required.", nameof(chatMessageId));
        }

        if (deliverable.Status != CampaignDeliverableStatus.Rendered)
        {
            throw new InvalidOperationException("Only a finished video can be revised.");
        }

        return new CampaignVideoRevision(
            deliverable.OrganizationId,
            deliverable.PropertyId,
            deliverable.CampaignGenerationJobId,
            deliverable.Id,
            chatMessageId,
            requestedByUserId.Trim(),
            deliverable.Version,
            editsJson,
            now);
    }

    public void Begin(DateTimeOffset now, TimeSpan lease)
    {
        if (Status is not (CampaignVideoRevisionStatus.Queued or CampaignVideoRevisionStatus.Running))
        {
            throw new InvalidOperationException("Only a waiting revision can start.");
        }

        Status = CampaignVideoRevisionStatus.Running;
        AttemptCount++;
        LeaseExpiresAtUtc = now.Add(lease);
        NextAttemptAtUtc = null;
        UpdatedAtUtc = now;
    }

    /// <summary>Applies the render to the video and remembers what it replaced.</summary>
    public void Complete(
        CampaignDeliverable deliverable,
        string specificationJson,
        Guid videoNarrationId,
        string assetPath,
        string contentType,
        long fileSize,
        int resultVersion,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(deliverable);
        if (deliverable.Id != CampaignDeliverableId || Status != CampaignVideoRevisionStatus.Running)
        {
            throw new InvalidOperationException("This revision is not running for that video.");
        }

        PreviousSpecificationJson = deliverable.SpecificationJson;
        PreviousVideoNarrationId = deliverable.VideoNarrationId;
        PreviousAssetPath = deliverable.AssetPath;
        PreviousContentType = deliverable.ContentType;
        PreviousFileSize = deliverable.FileSize;
        deliverable.ApplyRevision(specificationJson, videoNarrationId, assetPath, contentType, fileSize, resultVersion, now);
        ResultVersion = resultVersion;
        Status = CampaignVideoRevisionStatus.Completed;
        LeaseExpiresAtUtc = null;
        LastError = null;
        CompletedAtUtc = now;
        UpdatedAtUtc = now;
    }

    public void ScheduleRetry(string error, DateTimeOffset now, TimeSpan delay)
    {
        Status = CampaignVideoRevisionStatus.Queued;
        LastError = Limit(error);
        LeaseExpiresAtUtc = null;
        NextAttemptAtUtc = now.Add(delay);
        UpdatedAtUtc = now;
    }

    public void Fail(string error, DateTimeOffset now)
    {
        Status = CampaignVideoRevisionStatus.Failed;
        LastError = Limit(error);
        LeaseExpiresAtUtc = null;
        NextAttemptAtUtc = null;
        UpdatedAtUtc = now;
    }

    /// <summary>Puts the video back the way it was before this revision.</summary>
    public void Undo(CampaignDeliverable deliverable, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(deliverable);
        if (Status != CampaignVideoRevisionStatus.Completed
            || deliverable.Id != CampaignDeliverableId
            || deliverable.Version != ResultVersion
            || PreviousSpecificationJson is null
            || PreviousVideoNarrationId is null
            || PreviousAssetPath is null
            || PreviousContentType is null
            || PreviousFileSize is null)
        {
            throw new InvalidOperationException("Only the latest change to a video can be undone.");
        }

        deliverable.ApplyRevision(
            PreviousSpecificationJson,
            PreviousVideoNarrationId.Value,
            PreviousAssetPath,
            PreviousContentType,
            PreviousFileSize.Value,
            BaseVersion,
            now);
        Status = CampaignVideoRevisionStatus.Undone;
        UpdatedAtUtc = now;
    }

    private static string Limit(string error)
    {
        var normalized = string.IsNullOrWhiteSpace(error) ? "The video could not be updated." : error.Trim();
        return normalized.Length > 500 ? normalized[..500] : normalized;
    }
}
