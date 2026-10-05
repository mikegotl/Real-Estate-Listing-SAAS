namespace ListingStudio.Domain.Billing;

public sealed class BillingWebhookReceipt
{
    private BillingWebhookReceipt()
    {
    }

    private BillingWebhookReceipt(
        string eventId,
        string eventType,
        string payloadSha256,
        DateTimeOffset eventCreatedUtc,
        DateTimeOffset processedAtUtc)
    {
        EventId = Required(eventId, 255, nameof(eventId));
        EventType = Required(eventType, 100, nameof(eventType));
        PayloadSha256 = Required(payloadSha256, 64, nameof(payloadSha256));
        EventCreatedUtc = eventCreatedUtc;
        ProcessedAtUtc = processedAtUtc;
    }

    public string EventId { get; private set; } = string.Empty;

    public string EventType { get; private set; } = string.Empty;

    public string PayloadSha256 { get; private set; } = string.Empty;

    public DateTimeOffset EventCreatedUtc { get; private set; }

    public DateTimeOffset ProcessedAtUtc { get; private set; }

    public static BillingWebhookReceipt Create(
        string eventId,
        string eventType,
        string payloadSha256,
        DateTimeOffset eventCreatedUtc,
        DateTimeOffset processedAtUtc) =>
        new(eventId, eventType, payloadSha256, eventCreatedUtc, processedAtUtc);

    private static string Required(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        return normalized.Length <= maximumLength
            ? normalized
            : throw new ArgumentOutOfRangeException(parameterName, $"Value cannot exceed {maximumLength} characters.");
    }
}
