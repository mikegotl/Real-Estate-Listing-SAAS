using ListingStudio.Application.Properties;
using ListingStudio.Domain.Properties;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class WalkthroughMapTests
{
    [Fact]
    public void EmptyStoryboardHasNoSpaces() => Assert.Empty(WalkthroughMap.Build([]).Spaces);

    [Fact]
    public void StopsWithoutNotesRunStraightIntoThePropertyAndAreMarkedAssumed()
    {
        var layout = WalkthroughMap.Build(
        [
            new(WalkthroughStoryboard.ExteriorFront, WalkthroughDirection.Left),
            new(WalkthroughStoryboard.EntryFoyer, null),
            new("Living room", null),
        ]);

        // The arrival stop ignores any note: there is nothing before it to be left of.
        Assert.Equal(
            [(0, 0, (WalkthroughDirection?)null, false), (0, -1, WalkthroughDirection.Ahead, true), (0, -2, WalkthroughDirection.Ahead, true)],
            layout.Spaces.Select(space => (space.Column, space.Row, space.Direction, space.IsAssumed)));
        Assert.Equal([0], layout.Levels);
    }

    [Fact]
    public void TurnsAreRelativeToTheWayTheBuyerIsWalking()
    {
        var layout = WalkthroughMap.Build(
        [
            new("Exterior front", null),
            new("Entry / foyer", WalkthroughDirection.Ahead),
            new("Living room", WalkthroughDirection.Left),
            new("Dining room", WalkthroughDirection.Ahead),
            new("Kitchen", WalkthroughDirection.Right),
            new("Pantry", WalkthroughDirection.Right),
        ]);

        // Entry is north of the front; turning left heads west; turning right twice heads north, then east.
        Assert.Equal(
            [(0, 0), (0, -1), (-1, -1), (-2, -1), (-2, -2), (-1, -2)],
            layout.Spaces.Select(space => (space.Column, space.Row)));
        Assert.All(layout.Spaces.Skip(1), space => Assert.False(space.IsAssumed));
    }

    [Fact]
    public void SpacesNeverOverlapAndKeepMovingTheSameWayPastATakenCell()
    {
        var layout = WalkthroughMap.Build(
        [
            new("Exterior front", null),
            new("Entry / foyer", null),
            new("Office", WalkthroughDirection.Right),
            new("Bathroom", WalkthroughDirection.Right),
            new("Bedroom", WalkthroughDirection.Right),
        ]);

        // Three right turns head west from the bathroom; that cell (0, 0) is the front, so the bedroom lands one further.
        Assert.Equal(
            [(0, 0), (0, -1), (1, -1), (1, 0), (-1, 0)],
            layout.Spaces.Select(space => (space.Column, space.Row)));
        Assert.Equal(
            layout.Spaces.Count,
            layout.Spaces.Select(space => (space.Level, space.Column, space.Row)).Distinct().Count());
    }

    [Fact]
    public void StairsMoveToAnotherLevelAboveOrBelowTheStairs()
    {
        var layout = WalkthroughMap.Build(
        [
            new("Exterior front", null),
            new("Entry / foyer", null),
            new("Stairs", WalkthroughDirection.Left),
            new("Primary bedroom", WalkthroughDirection.Upstairs),
            new("Primary bathroom", WalkthroughDirection.Ahead),
            new("Stairs back down", WalkthroughDirection.Downstairs),
            new("Basement", WalkthroughDirection.Downstairs),
        ]);

        var byName = layout.Spaces.ToDictionary(space => space.Name);
        Assert.Equal((1, -1, -1), (byName["Primary bedroom"].Level, byName["Primary bedroom"].Column, byName["Primary bedroom"].Row));
        Assert.Equal((1, -2, -1), (byName["Primary bathroom"].Level, byName["Primary bathroom"].Column, byName["Primary bathroom"].Row));
        Assert.Equal((0, -2, -1), (byName["Stairs back down"].Level, byName["Stairs back down"].Column, byName["Stairs back down"].Row));
        Assert.Equal(-1, byName["Basement"].Level);
        Assert.Equal([0, 1, -1], layout.Levels);
    }

    [Fact]
    public void OutdoorStopsWithoutANoteAreAssumedBackOnTheMainLevel()
    {
        var layout = WalkthroughMap.Build(
        [
            new("Exterior front", null),
            new("Entry / foyer", null),
            new("Primary bedroom", WalkthroughDirection.Upstairs),
            new("Primary bathroom", null),
            new("Backyard", null),
            new("Pool", null),
        ]);

        var byName = layout.Spaces.ToDictionary(space => space.Name);
        Assert.Equal(1, byName["Primary bathroom"].Level);
        Assert.Equal((0, WalkthroughDirection.Downstairs, true), (byName["Backyard"].Level, byName["Backyard"].Direction, byName["Backyard"].IsAssumed));
        Assert.Equal((0, WalkthroughDirection.Ahead, true), (byName["Pool"].Level, byName["Pool"].Direction, byName["Pool"].IsAssumed));

        // A direction the agent sets still wins.
        var upstairsDeck = WalkthroughMap.Build(
        [
            new("Exterior front", null),
            new("Primary bedroom", WalkthroughDirection.Upstairs),
            new("Patio / deck", WalkthroughDirection.Ahead),
        ]);
        Assert.Equal(1, upstairsDeck.Spaces[^1].Level);
    }

    [Theory]
    [InlineData(0, "Main level")]
    [InlineData(1, "Upper level")]
    [InlineData(2, "Upper level 2")]
    [InlineData(-1, "Lower level")]
    [InlineData(-2, "Lower level 2")]
    public void LevelsAreNamedFromTheArrivalLevel(int level, string expected) =>
        Assert.Equal(expected, WalkthroughMap.LevelName(level));

    [Fact]
    public void DirectionNotesValidateAndUpdate()
    {
        var organizationId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var created = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
        var note = WalkthroughStopDirection.Create(organizationId, propertyId, "  Kitchen ", WalkthroughDirection.Left, created);
        Assert.Equal("Kitchen", note.Stop);
        Assert.Equal(WalkthroughDirection.Left, note.Direction);

        note.SetDirection(WalkthroughDirection.Upstairs, created.AddMinutes(1));
        Assert.Equal(WalkthroughDirection.Upstairs, note.Direction);
        Assert.Equal(created.AddMinutes(1), note.UpdatedAtUtc);

        Assert.Throws<ArgumentException>(() => WalkthroughStopDirection.Create(
            Guid.Empty, propertyId, "Kitchen", WalkthroughDirection.Left, created));
        Assert.Throws<ArgumentOutOfRangeException>(() => WalkthroughStopDirection.Create(
            organizationId, propertyId, new string('x', PropertyMedia.MaximumWalkthroughStopLength + 1), WalkthroughDirection.Left, created));
        Assert.Throws<ArgumentOutOfRangeException>(() => note.SetDirection((WalkthroughDirection)42, created));
    }
}
