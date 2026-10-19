namespace ListingStudio.Domain.Administration;

public sealed class OperationalEvent
{
    private OperationalEvent()
    {
    }

    private OperationalEvent(
        string kind,
        string category,
        string outcome,
        DateTimeOffset occurredAtUtc)
    {
        Id = Guid.NewGuid();
        Kind = Required(kind, 50, nameof(kind));
        Category = Required(category, 100, nameof(category));
        Outcome = Required(outcome, 50, nameof(outcome));
        OccurredAtUtc = occurredAtUtc;
    }

    public Guid Id { get; private set; }
    public string Kind { get; private set; } = string.Empty;
    public string Category { get; private set; } = string.Empty;
    public string Outcome { get; private set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; private set; }

    public static OperationalEvent Create(
        string kind,
        string category,
        string outcome,
        DateTimeOffset occurredAtUtc) =>
        new(kind, category, outcome, occurredAtUtc);

    private static string Required(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var trimmed = value.Trim();
        return trimmed.Length <= maximumLength
            ? trimmed
            : throw new ArgumentOutOfRangeException(parameterName);
    }
}
