namespace ListingStudio.Domain.Administration;

/// <summary>
/// One error answer from an external provider (Higgsfield, HeyGen, OpenAI, ElevenLabs and others) during campaign
/// work: what was being done, the HTTP status, the provider's own reply with secrets removed, and the request that
/// was sent when it is known. Append-only; shown to platform administrators on the audit log.
/// </summary>
public sealed class ProviderErrorEvent
{
    public const int MaximumResponseLength = 2_000;
    public const int MaximumRequestLength = 40_000;

    private ProviderErrorEvent()
    {
    }

    public Guid Id { get; private set; }
    public DateTimeOffset OccurredAtUtc { get; private set; }
    public string Provider { get; private set; } = string.Empty;
    public string Action { get; private set; } = string.Empty;
    public int? StatusCode { get; private set; }
    public string? ResponseText { get; private set; }
    public string? ProviderRequestId { get; private set; }
    public string? RequestBody { get; private set; }
    public Guid? OrganizationId { get; private set; }
    public Guid? PropertyId { get; private set; }
    public Guid? CampaignGenerationJobId { get; private set; }
    public Guid? CampaignVideoVariantId { get; private set; }
    public string? Stage { get; private set; }
    public int? Attempt { get; private set; }
    public bool WillRetry { get; private set; }

    public static ProviderErrorEvent Create(
        string provider,
        string action,
        int? statusCode,
        string? responseText,
        string? providerRequestId,
        string? requestBody,
        DateTimeOffset occurredAtUtc,
        Guid? organizationId = null,
        Guid? propertyId = null,
        Guid? campaignGenerationJobId = null,
        Guid? campaignVideoVariantId = null,
        string? stage = null,
        int? attempt = null,
        bool willRetry = false) => new()
        {
            Id = Guid.NewGuid(),
            OccurredAtUtc = occurredAtUtc,
            Provider = Cut(provider, 100) ?? throw new ArgumentException("A provider is required.", nameof(provider)),
            Action = Cut(action, 300) ?? throw new ArgumentException("An action is required.", nameof(action)),
            StatusCode = statusCode,
            ResponseText = Cut(responseText, MaximumResponseLength),
            ProviderRequestId = Cut(providerRequestId, 200),
            RequestBody = Cut(requestBody, MaximumRequestLength),
            OrganizationId = organizationId,
            PropertyId = propertyId,
            CampaignGenerationJobId = campaignGenerationJobId,
            CampaignVideoVariantId = campaignVideoVariantId,
            Stage = Cut(stage, 100),
            Attempt = attempt,
            WillRetry = willRetry,
        };

    private static string? Cut(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maximumLength ? trimmed : trimmed[..maximumLength];
    }
}
