namespace ListingStudio.Application.Properties;

/// <summary>
/// The agent's room-by-room narration, written beside the walk-through clips on the walkthrough map, and the agent's
/// choice between it and the AI script from the property story.
/// </summary>
public interface IRoomNarrationService
{
    Task<RoomNarrationOverview> GetAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default);

    /// <summary>Saves the narration spoken as the tour walks into <paramref name="walkthroughStop"/>. Blank text removes it.</summary>
    Task<bool> SetAsync(
        string userId,
        Guid propertyId,
        string walkthroughStop,
        string? text,
        CancellationToken cancellationToken = default);

    /// <summary>Chooses whether campaign videos speak the room-by-room narration (true) or the AI script (false).</summary>
    Task<bool> ChooseSourceAsync(
        string userId,
        Guid propertyId,
        bool useRoomNarration,
        CancellationToken cancellationToken = default);
}

/// <param name="UseRoomNarration">The agent's choice; null until they have chosen.</param>
/// <param name="NarratedStopCount">Photographed stops that have narration, which is what a video can speak.</param>
/// <param name="WordCount">Words across those stops.</param>
public sealed record RoomNarrationOverview(
    IReadOnlyList<RoomNarrationItem> Narrations,
    bool? UseRoomNarration,
    int NarratedStopCount,
    int WordCount)
{
    public static RoomNarrationOverview Empty { get; } = new([], null, 0, 0);

    public bool HasNarration => NarratedStopCount > 0;

    /// <summary>Whether campaign videos will speak the room-by-room narration.</summary>
    public bool IsInUse => HasNarration && UseRoomNarration == true;

    public string? TextFor(string stop) => Narrations
        .FirstOrDefault(item => string.Equals(item.Stop, stop, StringComparison.OrdinalIgnoreCase))?.Text;
}

public sealed record RoomNarrationItem(string Stop, string Text, DateTimeOffset UpdatedAtUtc);
