using ListingStudio.Domain.Organizations;

namespace ListingStudio.Domain.Properties;

/// <summary>Which way a buyer walks from the previous storyboard stop to reach a stop.</summary>
public enum WalkthroughDirection
{
    Ahead,
    Left,
    Right,
    Upstairs,
    Downstairs,
}

/// <summary>
/// The agent's note on how a walkthrough stop sits relative to the stop before it, such as "the kitchen is to the
/// left of the living room". The walkthrough map is drawn from these notes and the storyboard order.
/// </summary>
public sealed class WalkthroughStopDirection
{
    private WalkthroughStopDirection()
    {
    }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid PropertyId { get; private set; }
    public string Stop { get; private set; } = string.Empty;
    public WalkthroughDirection Direction { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public Organization Organization { get; private set; } = null!;
    public ListingProperty Property { get; private set; } = null!;

    public static WalkthroughStopDirection Create(
        Guid organizationId,
        Guid propertyId,
        string stop,
        WalkthroughDirection direction,
        DateTimeOffset updatedAtUtc)
    {
        if (organizationId == Guid.Empty || propertyId == Guid.Empty)
        {
            throw new ArgumentException("An organization and property are required.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(stop);
        var normalizedStop = stop.Trim();
        if (normalizedStop.Length > PropertyMedia.MaximumWalkthroughStopLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stop),
                $"A walkthrough stop cannot exceed {PropertyMedia.MaximumWalkthroughStopLength} characters.");
        }

        var created = new WalkthroughStopDirection
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            PropertyId = propertyId,
            Stop = normalizedStop,
        };
        created.SetDirection(direction, updatedAtUtc);
        return created;
    }

    public void SetDirection(WalkthroughDirection direction, DateTimeOffset updatedAtUtc)
    {
        if (!Enum.IsDefined(direction))
        {
            throw new ArgumentOutOfRangeException(nameof(direction), "Walkthrough direction is not supported.");
        }

        Direction = direction;
        UpdatedAtUtc = updatedAtUtc;
    }
}
