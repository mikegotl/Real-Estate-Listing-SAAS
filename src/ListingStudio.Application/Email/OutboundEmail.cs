namespace ListingStudio.Application.Email;

public enum OutboundEmailPriority
{
    Normal,
    High,
}

/// <summary>
/// A plain-text message. People who should not see each other's addresses are blind copied; a message to a single
/// person names them in <see cref="ToRecipients"/> instead.
/// </summary>
public sealed record OutboundEmail(
    IReadOnlyList<string> BlindCopyRecipients,
    string Subject,
    string TextBody,
    string? ReplyTo = null,
    OutboundEmailPriority Priority = OutboundEmailPriority.Normal,
    IReadOnlyList<string>? ToRecipients = null);

public interface IOutboundEmailSender
{
    /// <summary>False when no email provider is configured, so callers can leave work waiting instead of failing it.</summary>
    bool IsConfigured { get; }

    Task SendAsync(OutboundEmail email, CancellationToken cancellationToken = default);
}
