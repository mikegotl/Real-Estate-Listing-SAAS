namespace ListingStudio.Domain.Administration;

public sealed class PlatformAdminAuditEvent
{
    private PlatformAdminAuditEvent()
    {
    }

    private PlatformAdminAuditEvent(
        string action,
        string actorType,
        string targetReference,
        string outcome,
        DateTimeOffset occurredAtUtc)
    {
        Id = Guid.NewGuid();
        Action = Required(action, 100, nameof(action));
        ActorType = Required(actorType, 50, nameof(actorType));
        TargetReference = Required(targetReference, 64, nameof(targetReference));
        Outcome = Required(outcome, 32, nameof(outcome));
        OccurredAtUtc = occurredAtUtc;
    }

    public Guid Id { get; private set; }
    public string Action { get; private set; } = string.Empty;
    public string ActorType { get; private set; } = string.Empty;
    public string TargetReference { get; private set; } = string.Empty;
    public string Outcome { get; private set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; private set; }

    public static PlatformAdminAuditEvent Create(
        string action,
        string actorType,
        string targetReference,
        string outcome,
        DateTimeOffset occurredAtUtc) =>
        new(action, actorType, targetReference, outcome, occurredAtUtc);

    private static string Required(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var trimmed = value.Trim();
        return trimmed.Length <= maximumLength
            ? trimmed
            : throw new ArgumentOutOfRangeException(parameterName);
    }
}
