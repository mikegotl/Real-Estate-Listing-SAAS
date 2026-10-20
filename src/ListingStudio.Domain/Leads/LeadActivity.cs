namespace ListingStudio.Domain.Leads;

public enum LeadActivityKind
{
    Created,
    StatusChanged,
    Note,
}

public sealed class LeadActivity
{
    public const int MaximumTextLength = 2_000;

    private LeadActivity()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid LeadId { get; private set; }

    public LeadActivityKind Kind { get; private set; }

    public string Text { get; private set; } = string.Empty;

    public string? ActorUserId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public PropertyLead Lead { get; private set; } = null!;

    public static LeadActivity Create(
        Guid organizationId,
        Guid leadId,
        LeadActivityKind kind,
        string text,
        string? actorUserId,
        DateTimeOffset now)
    {
        if (organizationId == Guid.Empty || leadId == Guid.Empty)
        {
            throw new ArgumentException("An organization and lead are required.");
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var normalized = text.Trim();
        if (normalized.Length > MaximumTextLength)
        {
            throw new ArgumentOutOfRangeException(nameof(text), $"Activity text cannot exceed {MaximumTextLength} characters.");
        }

        return new LeadActivity
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            LeadId = leadId,
            Kind = kind,
            Text = normalized,
            ActorUserId = string.IsNullOrWhiteSpace(actorUserId) ? null : actorUserId.Trim(),
            CreatedAtUtc = now,
        };
    }
}
