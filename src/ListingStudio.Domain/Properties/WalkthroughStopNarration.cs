using ListingStudio.Domain.Organizations;

namespace ListingStudio.Domain.Properties;

/// <summary>
/// The agent's own words for one walkthrough stop, spoken as the tour walks into that room. Narration belongs to the
/// stop rather than to a clip, so remaking a walk-through clip keeps it.
/// </summary>
public sealed class WalkthroughStopNarration
{
    public const int MaximumTextLength = 1_000;

    private WalkthroughStopNarration()
    {
    }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid PropertyId { get; private set; }
    public string Stop { get; private set; } = string.Empty;
    public string Text { get; private set; } = string.Empty;
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public string UpdatedByUserId { get; private set; } = string.Empty;

    public Organization Organization { get; private set; } = null!;
    public ListingProperty Property { get; private set; } = null!;

    public static WalkthroughStopNarration Create(
        Guid organizationId,
        Guid propertyId,
        string stop,
        string text,
        string userId,
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

        var created = new WalkthroughStopNarration
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            PropertyId = propertyId,
            Stop = normalizedStop,
        };
        created.SetText(text, userId, updatedAtUtc);
        return created;
    }

    public void SetText(string text, string userId, DateTimeOffset updatedAtUtc)
    {
        Text = Normalize(text)
            ?? throw new ArgumentException("Narration text is required.", nameof(text));
        if (Text.Length > MaximumTextLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(text),
                $"Room narration cannot exceed {MaximumTextLength} characters.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        UpdatedByUserId = userId;
        UpdatedAtUtc = updatedAtUtc;
    }

    /// <summary>Trims the text and collapses runs of blank space, so a voice reads it as written. Blank is null.</summary>
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
