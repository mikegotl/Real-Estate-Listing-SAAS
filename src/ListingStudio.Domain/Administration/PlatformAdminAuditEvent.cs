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
        DateTimeOffset occurredAtUtc,
        string? actorReference)
    {
        Id = Guid.NewGuid();
        Action = Required(action, 100, nameof(action));
        ActorType = Required(actorType, 50, nameof(actorType));
        TargetReference = Required(targetReference, 64, nameof(targetReference));
        Outcome = Required(outcome, 32, nameof(outcome));
        OccurredAtUtc = occurredAtUtc;
        ActorReference = actorReference is null ? null : Required(actorReference, 64, nameof(actorReference));
    }

    public Guid Id { get; private set; }
    public string Action { get; private set; } = string.Empty;
    public string ActorType { get; private set; } = string.Empty;
    public string TargetReference { get; private set; } = string.Empty;
    public string Outcome { get; private set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; private set; }

    /// <summary>Pseudonymous reference to the administrator who acted, when a signed-in administrator acted.</summary>
    public string? ActorReference { get; private set; }

    public static PlatformAdminAuditEvent Create(
        string action,
        string actorType,
        string targetReference,
        string outcome,
        DateTimeOffset occurredAtUtc,
        string? actorReference = null) =>
        new(action, actorType, targetReference, outcome, occurredAtUtc, actorReference);

    private static string Required(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var trimmed = value.Trim();
        return trimmed.Length <= maximumLength
            ? trimmed
            : throw new ArgumentOutOfRangeException(parameterName);
    }
}
