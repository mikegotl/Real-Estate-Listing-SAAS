using ListingStudio.Domain.Properties;

namespace ListingStudio.Application.Properties;

/// <summary>
/// Ties each walk-through clip to the walking direction it was generated with. The direction is part of the clip's
/// generation version, so changing a stop's direction on the walkthrough map calls for a new clip, and the worker
/// always submits the direction the clip was requested with.
/// </summary>
public static class WalkthroughClipDirection
{
    private const string Marker = "/walk-";

    /// <summary>
    /// The direction a clip between two consecutive shots should follow: the next stop's direction when the clip
    /// crosses into a new stop, and none when both shots are in the same stop or the agent has not set one.
    /// </summary>
    public static WalkthroughDirection? ForPair(
        string? fromStop,
        string? toStop,
        IReadOnlyDictionary<string, WalkthroughDirection> stopDirections)
    {
        ArgumentNullException.ThrowIfNull(stopDirections);
        if (toStop is null || string.Equals(fromStop, toStop, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return stopDirections.TryGetValue(toStop, out var direction) ? direction : null;
    }

    /// <summary>
    /// The generation version for a clip. Clips without a direction keep the provider's version, so clips made before
    /// directions existed stay current until the agent sets a direction.
    /// </summary>
    public static string Version(string providerVersion, WalkthroughDirection? direction) => direction is { } value
        ? $"{providerVersion}{Marker}{value.ToString().ToLowerInvariant()}"
        : providerVersion;

    /// <summary>Reads the direction back from a clip's generation version.</summary>
    public static WalkthroughDirection? FromVersion(string generationVersion)
    {
        ArgumentNullException.ThrowIfNull(generationVersion);
        var index = generationVersion.LastIndexOf(Marker, StringComparison.Ordinal);
        return index >= 0
            && Enum.TryParse<WalkthroughDirection>(generationVersion[(index + Marker.Length)..], ignoreCase: true, out var direction)
            && Enum.IsDefined(direction)
                ? direction
                : null;
    }
}
