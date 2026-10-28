namespace ListingStudio.Domain.Administration;

/// <summary>
/// One email a platform administrator sent to one user. The row is the outbox the Worker sends from and the
/// permanent log of what was sent, to whom and by whom. Emails sent together share a <see cref="BatchId"/>.
/// </summary>
public sealed class AdminUserEmail
{
    public const int MaximumAttempts = 8;
    public const int MaximumSubjectLength = 200;
    public const int MaximumBodyLength = 10_000;
    public const int MaximumEmailLength = 256;
    public const int MaximumErrorLength = 500;

    private AdminUserEmail()
    {
    }

    private AdminUserEmail(
        Guid batchId,
        string sentByUserId,
        string recipientUserId,
        string recipientEmail,
        string subject,
        string body,
        DateTimeOffset queuedAtUtc)
    {
        if (batchId == Guid.Empty)
        {
            throw new ArgumentException("A batch identity is required.", nameof(batchId));
        }

        Id = Guid.NewGuid();
        BatchId = batchId;
        SentByUserId = Required(sentByUserId, 450, nameof(sentByUserId));
        RecipientUserId = Required(recipientUserId, 450, nameof(recipientUserId));
        RecipientEmail = Required(recipientEmail, MaximumEmailLength, nameof(recipientEmail));
        Subject = Required(subject, MaximumSubjectLength, nameof(subject));
        Body = Required(body, MaximumBodyLength, nameof(body));
        QueuedAtUtc = queuedAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid BatchId { get; private set; }

    public string SentByUserId { get; private set; } = string.Empty;

    public string RecipientUserId { get; private set; } = string.Empty;

    /// <summary>The address at the time of sending, kept even if the user later changes it.</summary>
    public string RecipientEmail { get; private set; } = string.Empty;

    public string Subject { get; private set; } = string.Empty;

    public string Body { get; private set; } = string.Empty;

    public DateTimeOffset QueuedAtUtc { get; private set; }

    public DateTimeOffset? SentAtUtc { get; private set; }

    public int AttemptCount { get; private set; }

    public DateTimeOffset? NextAttemptAtUtc { get; private set; }

    public string? LastError { get; private set; }

    public bool IsPending => SentAtUtc is null && AttemptCount < MaximumAttempts;

    public static AdminUserEmail Create(
        Guid batchId,
        string sentByUserId,
        string recipientUserId,
        string recipientEmail,
        string subject,
        string body,
        DateTimeOffset queuedAtUtc) =>
        new(batchId, sentByUserId, recipientUserId, recipientEmail, subject, body, queuedAtUtc);

    public void MarkSent(DateTimeOffset sentAtUtc)
    {
        SentAtUtc = sentAtUtc;
        AttemptCount++;
        NextAttemptAtUtc = null;
        LastError = null;
    }

    /// <summary>Records a failed send and backs off 1, 2, 4... minutes, capped at an hour. Returns whether it will be tried again.</summary>
    public bool RecordFailure(DateTimeOffset failedAtUtc, string error)
    {
        AttemptCount++;
        var delayMinutes = Math.Min(60, 1 << Math.Min(AttemptCount - 1, 6));
        NextAttemptAtUtc = failedAtUtc.AddMinutes(delayMinutes);
        var trimmed = (error ?? string.Empty).Trim();
        LastError = trimmed.Length <= MaximumErrorLength ? trimmed : trimmed[..MaximumErrorLength];
        return IsPending;
    }

    private static string Required(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var trimmed = value.Trim();
        return trimmed.Length <= maximumLength
            ? trimmed
            : throw new ArgumentOutOfRangeException(parameterName);
    }
}
