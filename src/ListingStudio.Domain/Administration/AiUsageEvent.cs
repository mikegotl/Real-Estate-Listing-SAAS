namespace ListingStudio.Domain.Administration;

public sealed class AiUsageEvent
{
    private AiUsageEvent()
    {
    }

    private AiUsageEvent(
        string provider,
        string model,
        string operation,
        bool succeeded,
        long durationMs,
        decimal estimatedCostUsd,
        string? failureCategory,
        DateTimeOffset occurredAtUtc,
        string? propertyReference)
    {
        Id = Guid.NewGuid();
        Provider = Required(provider, 50, nameof(provider));
        Model = Required(model, 100, nameof(model));
        Operation = Required(operation, 100, nameof(operation));
        Succeeded = succeeded;
        DurationMs = Math.Max(0, durationMs);
        EstimatedCostUsd = Math.Max(0, estimatedCostUsd);
        FailureCategory = Optional(failureCategory, 50, nameof(failureCategory));
        OccurredAtUtc = occurredAtUtc;
        PropertyReference = Optional(propertyReference, 64, nameof(propertyReference));
    }

    public Guid Id { get; private set; }
    public string Provider { get; private set; } = string.Empty;
    public string Model { get; private set; } = string.Empty;
    public string Operation { get; private set; } = string.Empty;
    public bool Succeeded { get; private set; }
    public long DurationMs { get; private set; }
    public decimal EstimatedCostUsd { get; private set; }
    public string? FailureCategory { get; private set; }
    public DateTimeOffset OccurredAtUtc { get; private set; }
    public string? PropertyReference { get; private set; }

    public static AiUsageEvent Create(
        string provider,
        string model,
        string operation,
        bool succeeded,
        long durationMs,
        decimal estimatedCostUsd,
        string? failureCategory,
        DateTimeOffset occurredAtUtc,
        string? propertyReference = null) =>
        new(provider, model, operation, succeeded, durationMs, estimatedCostUsd, failureCategory, occurredAtUtc, propertyReference);

    private static string Required(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var trimmed = value.Trim();
        return trimmed.Length <= maximumLength
            ? trimmed
            : throw new ArgumentOutOfRangeException(parameterName);
    }

    private static string? Optional(string? value, int maximumLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Required(value, maximumLength, parameterName);
    }
}
