using System.Text.RegularExpressions;
using ListingStudio.Domain.Properties;

namespace ListingStudio.Application.Properties;

/// <summary>
/// The guided photo storyboard: photos grouped into walkthrough stops in the order a buyer would move through the
/// home. The resulting display order is the sequence the video planner receives.
/// </summary>
public static partial class WalkthroughStoryboard
{
    public const string ExteriorFront = "Exterior front";
    public const string EntryFoyer = "Entry / foyer";

    /// <summary>Stops every storyboard starts with, always in this order.</summary>
    public static IReadOnlyList<string> TemplateStops { get; } = [ExteriorFront, EntryFoyer];

    /// <summary>Common next stops offered to the agent after the entry.</summary>
    public static IReadOnlyList<string> SuggestedStops { get; } =
    [
        "Living room",
        "Family room",
        "Dining room",
        "Kitchen",
        "Hallway",
        "Stairs",
        "Primary bedroom",
        "Primary bathroom",
        "Bedroom",
        "Bathroom",
        "Office",
        "Laundry",
        "Basement",
        "Garage",
        "Patio / deck",
        "Backyard",
        "Pool",
        "Rear exterior",
    ];

    /// <summary>
    /// Trims and collapses whitespace, matches template and suggested stops case-insensitively so "kitchen" and
    /// "Kitchen" share a stop, and returns null for a blank stop.
    /// </summary>
    public static string? Normalize(string? stop)
    {
        if (string.IsNullOrWhiteSpace(stop))
        {
            return null;
        }

        var normalized = Whitespace().Replace(stop.Trim(), " ");
        if (normalized.Length > PropertyMedia.MaximumWalkthroughStopLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stop),
                $"A walkthrough stop cannot exceed {PropertyMedia.MaximumWalkthroughStopLength} characters.");
        }

        return TemplateStops.Concat(SuggestedStops)
            .FirstOrDefault(known => string.Equals(known, normalized, StringComparison.OrdinalIgnoreCase))
            ?? normalized;
    }

    /// <summary>
    /// Groups items into stops. Template stops come first in template order, other stops follow in the order their
    /// earliest photo appears, and photos without a stop come last. Within a stop, photos keep their display order.
    /// Items that all lack a stop keep their existing order, so properties from before the storyboard are unchanged.
    /// </summary>
    public static IReadOnlyList<StoryboardStop<T>> Group<T>(
        IEnumerable<T> items,
        Func<T, string?> stopSelector,
        Func<T, int> orderSelector)
    {
        ArgumentNullException.ThrowIfNull(items);
        var ordered = items.OrderBy(orderSelector).ToArray();
        var labelled = ordered
            .Where(item => stopSelector(item) is not null)
            .GroupBy(item => stopSelector(item)!, StringComparer.OrdinalIgnoreCase)
            .Select((group, firstAppearance) => new
            {
                Stop = group.Key,
                Items = (IReadOnlyList<T>)group.ToArray(),
                TemplateIndex = IndexOfTemplate(group.Key),
                FirstAppearance = firstAppearance,
            })
            .OrderBy(group => group.TemplateIndex)
            .ThenBy(group => group.FirstAppearance)
            .Select(group => new StoryboardStop<T>(group.Stop, group.Items));
        var unsorted = ordered.Where(item => stopSelector(item) is null).ToArray();
        return unsorted.Length == 0
            ? [.. labelled]
            : [.. labelled, new StoryboardStop<T>(null, unsorted)];
    }

    /// <summary>Flattens <see cref="Group{T}"/> into the storyboard sequence.</summary>
    public static IReadOnlyList<T> Arrange<T>(
        IEnumerable<T> items,
        Func<T, string?> stopSelector,
        Func<T, int> orderSelector) => Group(items, stopSelector, orderSelector)
        .SelectMany(stop => stop.Items)
        .ToArray();

    /// <summary>Maps a photo analysis category to the stop it most likely belongs to, for one-click placement.</summary>
    public static string? SuggestStop(PropertyMediaCategory category) => category switch
    {
        PropertyMediaCategory.FrontExterior or PropertyMediaCategory.Aerial => ExteriorFront,
        PropertyMediaCategory.Entry => EntryFoyer,
        PropertyMediaCategory.LivingRoom => "Living room",
        PropertyMediaCategory.Kitchen => "Kitchen",
        PropertyMediaCategory.DiningRoom => "Dining room",
        PropertyMediaCategory.PrimaryBedroom => "Primary bedroom",
        PropertyMediaCategory.Bedroom => "Bedroom",
        PropertyMediaCategory.PrimaryBathroom => "Primary bathroom",
        PropertyMediaCategory.Bathroom => "Bathroom",
        PropertyMediaCategory.Office => "Office",
        PropertyMediaCategory.Laundry => "Laundry",
        PropertyMediaCategory.Garage => "Garage",
        PropertyMediaCategory.Pool => "Pool",
        PropertyMediaCategory.Patio => "Patio / deck",
        PropertyMediaCategory.Backyard => "Backyard",
        PropertyMediaCategory.RearExterior => "Rear exterior",
        _ => null,
    };

    public static bool IsTemplateStop(string? stop) => IndexOfTemplate(stop) < TemplateStops.Count;

    private static int IndexOfTemplate(string? stop)
    {
        for (var index = 0; index < TemplateStops.Count; index++)
        {
            if (string.Equals(TemplateStops[index], stop, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return TemplateStops.Count;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

public sealed record StoryboardStop<T>(string? Stop, IReadOnlyList<T> Items);
