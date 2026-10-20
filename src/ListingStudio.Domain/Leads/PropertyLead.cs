using System.ComponentModel.DataAnnotations;
using ListingStudio.Domain.Properties;

namespace ListingStudio.Domain.Leads;

public enum LeadStatus
{
    New,
    Contacted,
    Qualified,
    ShowingScheduled,
    Nurture,
    Won,
    Lost,
}

public enum LeadPreferredContact
{
    Email,
    Phone,
    Either,
}

public sealed class PropertyLead
{
    public const int MaximumNameLength = 120;
    public const int MaximumEmailLength = 320;
    public const int MaximumPhoneLength = 40;
    public const int MaximumMessageLength = 2_000;
    public const int MaximumAttributionLength = 100;
    public const int MaximumConsentVersionLength = 32;

    private PropertyLead()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid PropertyId { get; private set; }

    public Guid LeadCapturePageId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string Email { get; private set; } = string.Empty;

    public string? Phone { get; private set; }

    public LeadPreferredContact PreferredContact { get; private set; }

    public string Message { get; private set; } = string.Empty;

    public string? Source { get; private set; }

    public string? Medium { get; private set; }

    public string? Campaign { get; private set; }

    public bool EmailMarketingConsent { get; private set; }

    public bool SmsMarketingConsent { get; private set; }

    public string ConsentVersion { get; private set; } = string.Empty;

    public LeadStatus Status { get; private set; }

    public DateTimeOffset? FollowUpAtUtc { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public ListingProperty Property { get; private set; } = null!;

    public LeadCapturePage LeadCapturePage { get; private set; } = null!;

    public ICollection<LeadActivity> Activities { get; } = [];

    public static PropertyLead Create(
        Guid organizationId,
        Guid propertyId,
        Guid leadCapturePageId,
        string name,
        string email,
        string? phone,
        LeadPreferredContact preferredContact,
        string message,
        string? source,
        string? medium,
        string? campaign,
        bool emailMarketingConsent,
        bool smsMarketingConsent,
        string consentVersion,
        DateTimeOffset now)
    {
        if (organizationId == Guid.Empty || propertyId == Guid.Empty || leadCapturePageId == Guid.Empty)
        {
            throw new ArgumentException("Organization, property, and capture-page identities are required.");
        }

        if (!Enum.IsDefined(preferredContact))
        {
            throw new ArgumentOutOfRangeException(nameof(preferredContact));
        }

        var normalizedEmail = Required(email, MaximumEmailLength, nameof(email)).ToLowerInvariant();
        if (!new EmailAddressAttribute().IsValid(normalizedEmail))
        {
            throw new ArgumentException("Enter a valid email address.", nameof(email));
        }

        var normalizedPhone = Optional(phone, MaximumPhoneLength, nameof(phone));
        if (preferredContact is LeadPreferredContact.Phone or LeadPreferredContact.Either
            && normalizedPhone is null)
        {
            throw new ArgumentException("A phone number is required for the selected contact preference.", nameof(phone));
        }

        if (smsMarketingConsent && normalizedPhone is null)
        {
            throw new ArgumentException("SMS consent requires a phone number.", nameof(phone));
        }

        return new PropertyLead
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            PropertyId = propertyId,
            LeadCapturePageId = leadCapturePageId,
            Name = Required(name, MaximumNameLength, nameof(name)),
            Email = normalizedEmail,
            Phone = normalizedPhone,
            PreferredContact = preferredContact,
            Message = Required(message, MaximumMessageLength, nameof(message)),
            Source = Optional(source, MaximumAttributionLength, nameof(source)),
            Medium = Optional(medium, MaximumAttributionLength, nameof(medium)),
            Campaign = Optional(campaign, MaximumAttributionLength, nameof(campaign)),
            EmailMarketingConsent = emailMarketingConsent,
            SmsMarketingConsent = smsMarketingConsent,
            ConsentVersion = Required(consentVersion, MaximumConsentVersionLength, nameof(consentVersion)),
            Status = LeadStatus.New,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
    }

    public void UpdateWorkflow(LeadStatus status, DateTimeOffset? followUpAtUtc, DateTimeOffset now)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        Status = status;
        FollowUpAtUtc = followUpAtUtc;
        UpdatedAtUtc = now;
    }

    private static string Required(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return CheckLength(value.Trim(), maximumLength, parameterName);
    }

    private static string? Optional(string? value, int maximumLength, string parameterName) =>
        string.IsNullOrWhiteSpace(value) ? null : CheckLength(value.Trim(), maximumLength, parameterName);

    private static string CheckLength(string value, int maximumLength, string parameterName) =>
        value.Length <= maximumLength
            ? value
            : throw new ArgumentOutOfRangeException(parameterName, $"Value cannot exceed {maximumLength} characters.");
}
