using System.Data;
using System.Globalization;
using System.Text;
using ListingStudio.Application.Administration;
using ListingStudio.Application.Email;
using ListingStudio.Application.Feedback;
using ListingStudio.Infrastructure.Configuration;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ListingStudio.Infrastructure.Feedback;

/// <summary>
/// Emails each saved comment to every user in the platform admin role. The comment row records the send, so a
/// Worker restart neither loses nor repeats an email, and a failed send is retried with back-off.
/// </summary>
public sealed class FeatureSuggestionEmailProcessor(
    ApplicationDbContext dbContext,
    IOutboundEmailSender emailSender,
    IOptions<FeatureSuggestionEmailOptions> options,
    TimeProvider timeProvider) : IFeatureSuggestionEmailProcessor
{
    // When no admin has an email address yet, look again after this long instead of every poll.
    private static readonly TimeSpan NoRecipientsRecheck = TimeSpan.FromHours(1);

    public async Task<FeatureSuggestionEmailRunResult?> ProcessNextAsync(CancellationToken cancellationToken = default)
    {
        if (!emailSender.IsConfigured)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        // The row lock is held while sending, so two Worker replicas never email the same comment.
        var candidates = await dbContext.FeatureSuggestions.FromSqlInterpolated($"""
            SELECT *
            FROM "FeatureSuggestions"
            WHERE "AdminEmailSentAtUtc" IS NULL
              AND "AdminEmailAttemptCount" < {Domain.Feedback.FeatureSuggestion.MaximumAdminEmailAttempts}
              AND ("AdminEmailNextAttemptAtUtc" IS NULL OR "AdminEmailNextAttemptAtUtc" <= {now})
            ORDER BY "SubmittedAtUtc", "Id"
            FOR UPDATE SKIP LOCKED
            LIMIT 1
            """).ToListAsync(cancellationToken);
        var suggestion = candidates.SingleOrDefault();
        if (suggestion is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var recipients = await PlatformAdminEmailsAsync(cancellationToken);
        if (recipients.Count == 0)
        {
            suggestion.DeferAdminEmail(now.Add(NoRecipientsRecheck));
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new FeatureSuggestionEmailRunResult(
                suggestion.Id, FeatureSuggestionEmailOutcome.NoRecipients, 0, suggestion.AdminEmailAttemptCount, true, null);
        }

        var details = await (
                from candidate in dbContext.FeatureSuggestions.AsNoTracking()
                where candidate.Id == suggestion.Id
                join organization in dbContext.Organizations on candidate.OrganizationId equals organization.Id
                join user in dbContext.Users on candidate.SubmittedByUserId equals user.Id
                join property in dbContext.Properties on candidate.PropertyId equals property.Id into properties
                from property in properties.DefaultIfEmpty()
                select new CommentDetails(
                    organization.Name,
                    user.Email,
                    property == null
                        ? null
                        : property.Address1 + ", " + property.City + ", " + property.State + " " + property.ZipCode))
            .SingleAsync(cancellationToken);

        try
        {
            await emailSender.SendAsync(Compose(suggestion, details, recipients), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var willRetry = suggestion.RecordAdminEmailFailure(timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
            return new FeatureSuggestionEmailRunResult(
                suggestion.Id,
                FeatureSuggestionEmailOutcome.Failed,
                recipients.Count,
                suggestion.AdminEmailAttemptCount,
                willRetry,
                exception.Message);
        }

        suggestion.MarkAdminEmailSent(timeProvider.GetUtcNow());
        // The email has gone, so record it even if the Worker is stopping.
        await dbContext.SaveChangesAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
        return new FeatureSuggestionEmailRunResult(
            suggestion.Id, FeatureSuggestionEmailOutcome.Sent, recipients.Count, suggestion.AdminEmailAttemptCount, false, null);
    }

    private async Task<IReadOnlyList<string>> PlatformAdminEmailsAsync(CancellationToken cancellationToken)
    {
        var emails = await (
                from role in dbContext.Roles
                where role.Name == PlatformAdministration.Role
                join userRole in dbContext.UserRoles on role.Id equals userRole.RoleId
                join user in dbContext.Users on userRole.UserId equals user.Id
                where user.Email != null && user.Email != ""
                select user.Email!)
            .ToListAsync(cancellationToken);
        return emails
            .Select(email => email.Trim())
            .Where(email => MimeKit.MailboxAddress.TryParse(email, out _))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private OutboundEmail Compose(
        Domain.Feedback.FeatureSuggestion suggestion,
        CommentDetails details,
        IReadOnlyList<string> recipients)
    {
        var from = details.SubmitterEmail ?? "a user without an email address";
        var subject = suggestion.RequiredForActiveCampaign
            ? $"Listing Studio comment from {details.OrganizationName} (needed for an active campaign)"
            : $"Listing Studio comment from {details.OrganizationName}";

        var body = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"{from} ({details.OrganizationName}) sent a comment:")
            .AppendLine()
            .AppendLine(suggestion.Comment)
            .AppendLine()
            .AppendLine("----");
        if (details.PropertyAddress is not null)
        {
            body.AppendLine(CultureInfo.InvariantCulture, $"Property: {details.PropertyAddress}");
        }

        if (suggestion.RequiredForActiveCampaign)
        {
            body.AppendLine("They marked it as required for the campaign that is running now.");
        }

        var page = PageLink(suggestion.PagePath);
        if (page is not null)
        {
            body.AppendLine(CultureInfo.InvariantCulture, $"Sent from: {page}");
        }

        body.AppendLine(CultureInfo.InvariantCulture, $"Sent at: {suggestion.SubmittedAtUtc:yyyy-MM-dd HH:mm} UTC")
            .AppendLine()
            .AppendLine("You get this email because you are a Listing Studio platform admin. Reply to answer the sender.");

        return new OutboundEmail(recipients, subject, body.ToString(), details.SubmitterEmail);
    }

    private string? PageLink(string? pagePath)
    {
        if (string.IsNullOrWhiteSpace(pagePath))
        {
            return null;
        }

        return Uri.TryCreate(options.Value.AppBaseUrl, UriKind.Absolute, out var baseUri)
            && Uri.TryCreate(baseUri, pagePath, out var link)
            && link.Host == baseUri.Host
                ? link.ToString()
                : pagePath;
    }

    private sealed record CommentDetails(string OrganizationName, string? SubmitterEmail, string? PropertyAddress);
}
