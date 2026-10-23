namespace ListingStudio.Application.Email;

/// <summary>
/// A plain-text message to people who should not see each other's addresses, so every recipient is blind copied.
/// </summary>
public sealed record OutboundEmail(
    IReadOnlyList<string> BlindCopyRecipients,
    string Subject,
    string TextBody,
    string? ReplyTo = null);

public interface IOutboundEmailSender
{
    /// <summary>False when no email provider is configured, so callers can leave work waiting instead of failing it.</summary>
    bool IsConfigured { get; }

    Task SendAsync(OutboundEmail email, CancellationToken cancellationToken = default);
}
