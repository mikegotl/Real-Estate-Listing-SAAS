namespace ListingStudio.Application.Administration;

public sealed record AdminEmailRecipient(string UserId, string? Email, string UserName);

public sealed record AdminUserEmailRequest(
    IReadOnlyList<string> RecipientUserIds,
    string Subject,
    string Body);

public enum AdminUserEmailFailure
{
    None = 0,
    NotPlatformAdmin = 1,
    NoRecipients = 2,
    TooManyRecipients = 3,
    SubjectRequired = 4,
    SubjectTooLong = 5,
    BodyRequired = 6,
    BodyTooLong = 7,
}

/// <param name="QueuedCount">Emails queued, one per recipient with a usable address.</param>
/// <param name="SkippedCount">Selected users who have no usable email address or no longer exist.</param>
public sealed record AdminUserEmailQueueResult(AdminUserEmailFailure Failure, int QueuedCount, int SkippedCount)
{
    public bool Succeeded => Failure == AdminUserEmailFailure.None;
}

public enum AdminUserEmailStatus
{
    Queued,
    Sent,
    Failed,
}

/// <summary>One email an administrator sent to one user, as listed in the sent email log.</summary>
public sealed record AdminUserEmailLogRow(
    Guid Id,
    Guid BatchId,
    DateTimeOffset QueuedAtUtc,
    string? SentByEmail,
    string RecipientEmail,
    string Subject,
    string Body,
    AdminUserEmailStatus Status,
    DateTimeOffset? SentAtUtc,
    int AttemptCount,
    string? LastError);

/// <summary>
/// Lets platform administrators email one user or a selection of users. Emails are queued here and sent by the
/// Worker over SMTP, one message per recipient, so recipients never see each other's addresses. Every email is
/// kept in the sent email log and each recipient gets an audit event.
/// </summary>
public interface IAdminUserEmailService
{
    public const int MaximumRecipients = 100;
    public const int MaximumSubjectLength = 200;
    public const int MaximumBodyLength = 10_000;

    Task<IReadOnlyList<AdminEmailRecipient>> GetRecipientsAsync(
        IReadOnlyList<string> userIds,
        CancellationToken cancellationToken = default);

    Task<AdminUserEmailQueueResult> QueueAsync(
        string administratorUserId,
        AdminUserEmailRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AdminUserEmailLogRow>> GetLogAsync(
        int maximumResults = 100,
        CancellationToken cancellationToken = default);
}

public enum AdminUserEmailSendOutcome
{
    Sent,
    Failed,
}

public sealed record AdminUserEmailRunResult(
    Guid EmailId,
    AdminUserEmailSendOutcome Outcome,
    int AttemptNumber,
    bool WillRetry,
    string? Error);

/// <summary>Sends the next queued administrator email; returns null when nothing is waiting or SMTP is not configured.</summary>
public interface IAdminUserEmailProcessor
{
    Task<AdminUserEmailRunResult?> ProcessNextAsync(CancellationToken cancellationToken = default);
}

public static class AdminUserEmailAudit
{
    public const string QueueAction = "platform-admin.email.queue";
}
