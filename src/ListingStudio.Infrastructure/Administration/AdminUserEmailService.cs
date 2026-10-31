using System.Data;
using ListingStudio.Application.Administration;
using ListingStudio.Application.Email;
using ListingStudio.Domain.Administration;
using ListingStudio.Infrastructure.Configuration;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace ListingStudio.Infrastructure.Administration;

public sealed class AdminUserEmailService(
    ApplicationDbContext dbContext,
    TimeProvider timeProvider) : IAdminUserEmailService
{
    public async Task<IReadOnlyList<AdminEmailRecipient>> GetRecipientsAsync(
        IReadOnlyList<string> userIds,
        CancellationToken cancellationToken = default)
    {
        var ids = DistinctIds(userIds);
        if (ids.Count == 0)
        {
            return [];
        }

        return await dbContext.Users.AsNoTracking()
            .Where(user => ids.Contains(user.Id))
            .OrderBy(user => user.Email)
            .Select(user => new AdminEmailRecipient(user.Id, user.Email, user.UserName ?? string.Empty))
            .ToListAsync(cancellationToken);
    }

    public async Task<AdminUserEmailQueueResult> QueueAsync(
        string administratorUserId,
        AdminUserEmailRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(administratorUserId);
        ArgumentNullException.ThrowIfNull(request);

        var isAdministrator = await (
                from userRole in dbContext.UserRoles
                join role in dbContext.Roles on userRole.RoleId equals role.Id
                where role.Name == PlatformAdministration.Role && userRole.UserId == administratorUserId
                select userRole.UserId)
            .AnyAsync(cancellationToken);
        if (!isAdministrator)
        {
            return Failed(AdminUserEmailFailure.NotPlatformAdmin);
        }

        // A subject is one line; pasted line breaks would otherwise be rejected by the mail server.
        var subject = string.Join(
            ' ',
            (request.Subject ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var body = (request.Body ?? string.Empty).Trim();
        var ids = DistinctIds(request.RecipientUserIds);
        var failure = ids.Count == 0 ? AdminUserEmailFailure.NoRecipients
            : ids.Count > IAdminUserEmailService.MaximumRecipients ? AdminUserEmailFailure.TooManyRecipients
            : subject.Length == 0 ? AdminUserEmailFailure.SubjectRequired
            : subject.Length > IAdminUserEmailService.MaximumSubjectLength ? AdminUserEmailFailure.SubjectTooLong
            : body.Length == 0 ? AdminUserEmailFailure.BodyRequired
            : body.Length > IAdminUserEmailService.MaximumBodyLength ? AdminUserEmailFailure.BodyTooLong
            : AdminUserEmailFailure.None;
        if (failure != AdminUserEmailFailure.None)
        {
            return Failed(failure);
        }

        var recipients = await GetRecipientsAsync(ids, cancellationToken);
        var deliverable = recipients
            .Select(recipient => new { recipient.UserId, Email = recipient.Email?.Trim() })
            .Where(recipient => !string.IsNullOrEmpty(recipient.Email)
                && recipient.Email.Length <= AdminUserEmail.MaximumEmailLength
                && MimeKit.MailboxAddress.TryParse(recipient.Email, out _))
            .ToList();
        if (deliverable.Count == 0)
        {
            return new AdminUserEmailQueueResult(AdminUserEmailFailure.NoRecipients, 0, ids.Count);
        }

        var now = timeProvider.GetUtcNow();
        var batchId = Guid.NewGuid();
        var actor = AuditPseudonym.For(administratorUserId);
        foreach (var recipient in deliverable)
        {
            dbContext.AdminUserEmails.Add(AdminUserEmail.Create(
                batchId,
                administratorUserId,
                recipient.UserId,
                recipient.Email!,
                subject,
                body,
                now));
            dbContext.PlatformAdminAuditEvents.Add(PlatformAdminAuditEvent.Create(
                AdminUserEmailAudit.QueueAction,
                "platform-admin",
                AuditPseudonym.For(recipient.UserId),
                "queued",
                now,
                actor));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return new AdminUserEmailQueueResult(AdminUserEmailFailure.None, deliverable.Count, ids.Count - deliverable.Count);
    }

    public async Task<IReadOnlyList<AdminUserEmailLogRow>> GetLogAsync(
        int maximumResults = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumResults, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumResults, 500);
        var rows = await (
                from email in dbContext.AdminUserEmails.AsNoTracking()
                join sender in dbContext.Users on email.SentByUserId equals sender.Id into senders
                from sender in senders.DefaultIfEmpty()
                orderby email.QueuedAtUtc descending, email.RecipientEmail
                select new
                {
                    email.Id,
                    email.BatchId,
                    email.QueuedAtUtc,
                    SentByEmail = sender == null ? null : sender.Email,
                    email.RecipientEmail,
                    email.Subject,
                    email.Body,
                    email.BodyIsProtected,
                    email.SentAtUtc,
                    email.AttemptCount,
                    email.LastError,
                })
            .Take(maximumResults)
            .ToListAsync(cancellationToken);
        return rows
            .Select(row => new AdminUserEmailLogRow(
                row.Id,
                row.BatchId,
                row.QueuedAtUtc,
                row.SentByEmail,
                row.RecipientEmail,
                row.Subject,
                row.BodyIsProtected ? "Sensitive invitation details are hidden." : row.Body,
                row.SentAtUtc is not null ? AdminUserEmailStatus.Sent
                    : row.AttemptCount >= AdminUserEmail.MaximumAttempts ? AdminUserEmailStatus.Failed
                    : AdminUserEmailStatus.Queued,
                row.SentAtUtc,
                row.AttemptCount,
                row.LastError))
            .ToList();
    }

    private static List<string> DistinctIds(IReadOnlyList<string>? userIds) =>
        (userIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static AdminUserEmailQueueResult Failed(AdminUserEmailFailure failure) => new(failure, 0, 0);
}

/// <summary>
/// Sends queued administrator emails one at a time. The row lock is held while sending, so two Worker replicas
/// never send the same email, and the row records the send so a restart neither loses nor repeats one.
/// </summary>
public sealed class AdminUserEmailProcessor(
    ApplicationDbContext dbContext,
    IOutboundEmailSender emailSender,
    TimeProvider timeProvider,
    IDataProtectionProvider? dataProtectionProvider = null,
    IOptions<TesterInvitationOptions>? invitationOptions = null) : IAdminUserEmailProcessor
{
    public async Task<AdminUserEmailRunResult?> ProcessNextAsync(CancellationToken cancellationToken = default)
    {
        if (!emailSender.IsConfigured)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var candidates = await dbContext.AdminUserEmails.FromSqlInterpolated($"""
            SELECT *
            FROM "AdminUserEmails"
            WHERE "SentAtUtc" IS NULL
              AND "AttemptCount" < {AdminUserEmail.MaximumAttempts}
              AND ("NextAttemptAtUtc" IS NULL OR "NextAttemptAtUtc" <= {now})
            ORDER BY "QueuedAtUtc", "Id"
            FOR UPDATE SKIP LOCKED
            LIMIT 1
            """).ToListAsync(cancellationToken);
        var email = candidates.SingleOrDefault();
        if (email is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var replyTo = await dbContext.Users.AsNoTracking()
            .Where(user => user.Id == email.SentByUserId)
            .Select(user => user.Email)
            .SingleOrDefaultAsync(cancellationToken);
        try
        {
            var textBody = Compose(email, dataProtectionProvider);
            var htmlBody = string.Equals(email.Subject, PlatformTesters.WelcomeEmailSubject, StringComparison.Ordinal)
                ? TesterWelcomeEmailTemplate.CreateHtml(
                    textBody,
                    TesterWelcomeEmailTemplate.LoginUrl(invitationOptions?.Value.AppBaseUrl))
                : null;
            await emailSender.SendAsync(
                new OutboundEmail(
                    [],
                    email.Subject,
                    textBody,
                    replyTo,
                    ToRecipients: [email.RecipientEmail],
                    HtmlBody: htmlBody),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var willRetry = email.RecordFailure(timeProvider.GetUtcNow(), exception.Message);
            await dbContext.SaveChangesAsync(CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
            return new AdminUserEmailRunResult(
                email.Id, AdminUserEmailSendOutcome.Failed, email.AttemptCount, willRetry, exception.Message);
        }

        email.MarkSent(timeProvider.GetUtcNow());
        // The email has gone, so record it even if the Worker is stopping.
        await dbContext.SaveChangesAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
        return new AdminUserEmailRunResult(email.Id, AdminUserEmailSendOutcome.Sent, email.AttemptCount, false, null);
    }

    private static string Compose(AdminUserEmail email, IDataProtectionProvider? dataProtectionProvider) =>
        Body(email, dataProtectionProvider)
        + Environment.NewLine + Environment.NewLine
        + "----" + Environment.NewLine
        + "Sent by the Creative Listing Studio team because you have a Creative Listing Studio account. Reply to this email to reach us.";

    private static string Body(AdminUserEmail email, IDataProtectionProvider? dataProtectionProvider)
    {
        if (!email.BodyIsProtected)
        {
            return email.Body;
        }

        if (dataProtectionProvider is null)
        {
            throw new InvalidOperationException("Sensitive invitation email protection is unavailable.");
        }

        return TesterInvitationEmailProtection.CreateProtector(dataProtectionProvider).Unprotect(email.Body);
    }
}
