using ListingStudio.Application.Email;
using ListingStudio.Infrastructure.Configuration;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace ListingStudio.Infrastructure.Email;

public sealed class SmtpOutboundEmailSender(IOptions<SmtpEmailOptions> options) : IOutboundEmailSender
{
    public bool IsConfigured => options.Value.IsConfigured;

    public async Task SendAsync(OutboundEmail email, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);
        var settings = options.Value;
        if (!settings.IsConfigured)
        {
            throw new InvalidOperationException("Email:Smtp:Host and Email:Smtp:FromAddress must be set before sending email.");
        }

        var toRecipients = email.ToRecipients ?? [];
        if (email.BlindCopyRecipients.Count == 0 && toRecipients.Count == 0)
        {
            throw new ArgumentException("An email needs at least one recipient.", nameof(email));
        }

        var from = new MailboxAddress(settings.FromName, settings.FromAddress);
        var message = new MimeMessage();
        message.From.Add(from);
        if (toRecipients.Count == 0)
        {
            // Recipients are blind copied so admins never see each other's addresses; the visible To is the sender.
            message.To.Add(from);
        }

        foreach (var recipient in toRecipients)
        {
            message.To.Add(MailboxAddress.Parse(recipient));
        }

        foreach (var recipient in email.BlindCopyRecipients)
        {
            message.Bcc.Add(MailboxAddress.Parse(recipient));
        }

        if (!string.IsNullOrWhiteSpace(email.ReplyTo) && MailboxAddress.TryParse(email.ReplyTo, out var replyTo))
        {
            message.ReplyTo.Add(replyTo);
        }

        message.Subject = email.Subject;
        message.Body = new TextPart("plain") { Text = email.TextBody };
        if (email.Priority == OutboundEmailPriority.High)
        {
            message.Importance = MessageImportance.High;
            message.Priority = MessagePriority.Urgent;
            message.Headers[HeaderId.XPriority] = "1 (Highest)";
        }

        using var client = new SmtpClient { Timeout = settings.TimeoutSeconds * 1000 };
        // Port 465 is implicit TLS; anything else upgrades with STARTTLS when the server offers it.
        var security = settings.Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;
        await client.ConnectAsync(settings.Host, settings.Port, security, cancellationToken);
        if (!string.IsNullOrWhiteSpace(settings.Username))
        {
            await client.AuthenticateAsync(settings.Username, settings.Password, cancellationToken);
        }

        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);
    }
}
