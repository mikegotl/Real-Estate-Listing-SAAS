using ListingStudio.Domain.Properties;

namespace ListingStudio.Application.Properties;

/// <summary>
/// A schematic map of the walkthrough: each stop becomes a space on a grid, placed next to the stop before it in
/// the direction the agent noted. There is no floor plan behind it, so it shows how the spaces connect in walking
/// order, not their size or exact position.
/// </summary>
public static class WalkthroughMap
{
    // Grid headings, clockwise from "into the property", which is up on the map.
    private static readonly (int Column, int Row)[] Headings = [(0, -1), (1, 0), (0, 1), (-1, 0)];

    /// <summary>
    /// Lays out stops in storyboard order. The first stop is where the buyer arrives; every later stop moves from
    /// the previous one in its noted direction, relative to the way the buyer is walking. A stop without a note is
    /// assumed to be straight ahead, or back on the main level for an outdoor stop. When the neighbouring cell is taken, the space moves further the same way.
    /// </summary>
    public static WalkthroughMapLayout Build(IReadOnlyList<WalkthroughMapStop> stops)
    {
        ArgumentNullException.ThrowIfNull(stops);
        var spaces = new List<WalkthroughMapSpace>(stops.Count);
        var occupied = new HashSet<(int Level, int Column, int Row)>();
        var heading = 0;
        for (var index = 0; index < stops.Count; index++)
        {
            var stop = stops[index];
            if (index == 0)
            {
                spaces.Add(new WalkthroughMapSpace(index, stop.Name, 0, 0, 0, null, IsAssumed: false));
                occupied.Add((0, 0, 0));
                continue;
            }

            var previous = spaces[^1];
            var direction = stop.Direction ?? AssumedDirection(stop.Name, previous.Level);
            var level = previous.Level;
            var returnsToMainLevel = stop.Direction is null && direction != WalkthroughDirection.Ahead;
            switch (direction)
            {
                case WalkthroughDirection.Left:
                    heading = (heading + 3) % 4;
                    break;
                case WalkthroughDirection.Right:
                    heading = (heading + 1) % 4;
                    break;
                case WalkthroughDirection.Upstairs:
                    level = returnsToMainLevel ? 0 : level + 1;
                    break;
                case WalkthroughDirection.Downstairs:
                    level = returnsToMainLevel ? 0 : level - 1;
                    break;
            }

            var (columnStep, rowStep) = Headings[heading];
            var changesLevel = level != previous.Level;
            var column = changesLevel ? previous.Column : previous.Column + columnStep;
            var row = changesLevel ? previous.Row : previous.Row + rowStep;
            while (occupied.Contains((level, column, row)))
            {
                column += columnStep;
                row += rowStep;
            }

            occupied.Add((level, column, row));
            spaces.Add(new WalkthroughMapSpace(index, stop.Name, level, column, row, direction, stop.Direction is null));
        }

        return new WalkthroughMapLayout(spaces);
    }

    // Without a note a stop is straight ahead, except an outdoor stop such as the backyard after an upper or lower
    // level, which is assumed to be back down (or up) on the main level.
    private static WalkthroughDirection AssumedDirection(string stop, int previousLevel) =>
        previousLevel == 0 || !WalkthroughStoryboard.IsGroundLevelOutdoor(stop)
            ? WalkthroughDirection.Ahead
            : previousLevel > 0 ? WalkthroughDirection.Downstairs : WalkthroughDirection.Upstairs;

    /// <summary>How a step reads to the agent, such as "turn left" or "up the stairs".</summary>
    public static string Describe(WalkthroughDirection direction) => direction switch
    {
        WalkthroughDirection.Ahead => "straight ahead",
        WalkthroughDirection.Left => "turn left",
        WalkthroughDirection.Right => "turn right",
        WalkthroughDirection.Upstairs => "up the stairs",
        WalkthroughDirection.Downstairs => "down the stairs",
        _ => throw new ArgumentOutOfRangeException(nameof(direction)),
    };

    /// <summary>A level's name on the map: the arrival level is the main level.</summary>
    public static string LevelName(int level) => level switch
    {
        0 => "Main level",
        1 => "Upper level",
        -1 => "Lower level",
        > 1 => $"Upper level {level}",
        _ => $"Lower level {-level}",
    };
}

public sealed record WalkthroughMapStop(string Name, WalkthroughDirection? Direction);

/// <summary>
/// One space on the map. <see cref="Direction"/> is the way the buyer walks from the previous space (null for the
/// first), and <see cref="IsAssumed"/> marks a direction the agent has not set yet.
/// </summary>
public sealed record WalkthroughMapSpace(
    int Index,
    string Name,
    int Level,
    int Column,
    int Row,
    WalkthroughDirection? Direction,
    bool IsAssumed);

public sealed record WalkthroughMapLayout(IReadOnlyList<WalkthroughMapSpace> Spaces)
{
    /// <summary>Levels in display order: the main level first, then upper levels, then lower levels.</summary>
    public IReadOnlyList<int> Levels => Spaces
        .Select(space => space.Level)
        .Distinct()
        .OrderBy(level => level < 0 ? 1 : 0)
        .ThenBy(level => Math.Abs(level))
        .ToArray();
}
