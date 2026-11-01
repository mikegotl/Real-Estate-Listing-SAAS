using ListingStudio.Application.Campaigns;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Properties;

namespace ListingStudio.TestSupport;

/// <summary>
/// An eight-stop listing like the ones in the render tests, plus optional rich inputs. Shared by the unit and
/// integration tests.
/// </summary>
internal static class RecipeFixture
{
    public static readonly Guid Front = Id(1);
    public static readonly Guid Entry = Id(2);
    public static readonly Guid Kitchen = Id(3);
    public static readonly Guid Living = Id(4);
    public static readonly Guid Dining = Id(5);
    public static readonly Guid Bedroom = Id(6);
    public static readonly Guid Bath = Id(7);
    public static readonly Guid Patio = Id(8);
    public static readonly Guid Before1 = Id(9);
    public static readonly Guid Before2 = Id(10);
    public static readonly Guid After1 = Id(11);
    public static readonly Guid After2 = Id(12);
    public static readonly Guid IntroVideo = Id(20);
    public static readonly Guid WalkVideo = Id(21);

    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private static readonly (Guid Id, string Stop, PropertyMediaCategory Category, int Hero, int Quality, bool Exterior)[] Stops =
    [
        (Front, "Exterior front", PropertyMediaCategory.FrontExterior, 66, 78, true),
        (Entry, "Entry / foyer", PropertyMediaCategory.Entry, 50, 70, false),
        (Kitchen, "Kitchen", PropertyMediaCategory.Kitchen, 88, 85, false),
        (Living, "Living room", PropertyMediaCategory.LivingRoom, 72, 80, false),
        (Dining, "Dining room", PropertyMediaCategory.DiningRoom, 55, 70, false),
        (Bedroom, "Primary bedroom", PropertyMediaCategory.PrimaryBedroom, 50, 72, false),
        (Bath, "Bathroom", PropertyMediaCategory.Bathroom, 40, 68, false),
        (Patio, "Back patio", PropertyMediaCategory.Patio, 66, 74, true),
    ];

    public static VerticalListingFacts Facts() => new(PropertyType.SingleFamily, 450_000m, 3, 2.5m, 2_100, 0.25m, "Raleigh", "NC", 2);

    public static IReadOnlyList<VerticalRolePhoto> RolePhotos() => Stops
        .Select((stop, index) => new VerticalRolePhoto(stop.Id, stop.Category, true, stop.Quality, stop.Hero, stop.Exterior, index))
        .ToArray();

    public static IReadOnlyList<VerticalRoleFootage> RoleFootage() =>
    [
        new VerticalRoleFootage(WalkVideo, 40_000, true, true, PropertyMediaCategory.LivingRoom),
    ];

    public static IReadOnlyList<VerticalNarrationLine> Narration() =>
    [
        new("room.narration.1", "Welcome to my favorite listing this fall.", "Exterior front"),
        new("room.narration.2", "The kitchen island seats four.", "Kitchen"),
        new("room.narration.3", "The living room gets afternoon light.", "Living room"),
        new("room.narration.4", "Out back the patio is ready for summer.", "Back patio"),
    ];

    /// <summary>Photos and a walk-through video, with suggested roles and the given agent decisions.</summary>
    public static VerticalPlanningInput Input(Guid? heroOverride = null, bool removeHero = false, bool rich = false)
    {
        var rolePhotos = RolePhotos().ToList();
        var stops = Stops.ToList();
        if (rich)
        {
            stops.AddRange([
                (Before1, "Kitchen", PropertyMediaCategory.Kitchen, 30, 62, false),
                (Before2, "Kitchen", PropertyMediaCategory.Kitchen, 28, 60, false),
                (After1, "Kitchen", PropertyMediaCategory.Kitchen, 80, 84, false),
                (After2, "Living room", PropertyMediaCategory.LivingRoom, 70, 80, false),
            ]);
            rolePhotos = stops.Select((stop, index) =>
                new VerticalRolePhoto(stop.Id, stop.Category, true, stop.Quality, stop.Hero, stop.Exterior, index)).ToList();
        }

        var footage = RoleFootage().ToList();
        if (rich)
        {
            footage.Insert(0, new VerticalRoleFootage(IntroVideo, 6_000, true, true, null));
        }

        var overrides = new List<VerticalMediaRoleOverride>();
        if (heroOverride is { } hero)
        {
            overrides.Add(new(hero, VerticalMediaRole.HeroFeature, VerticalMediaRoleDecision.Assigned, "agent", Now));
            overrides.Add(new(Kitchen, VerticalMediaRole.HeroFeature, VerticalMediaRoleDecision.Removed, "agent", Now));
        }

        if (removeHero)
        {
            overrides.Add(new(Kitchen, VerticalMediaRole.HeroFeature, VerticalMediaRoleDecision.Removed, "agent", Now));
        }

        if (rich)
        {
            overrides.Add(new(Kitchen, VerticalMediaRole.HeroFeature, VerticalMediaRoleDecision.Assigned, "agent", Now));
            overrides.Add(new(Before1, VerticalMediaRole.BeforeRenovation, VerticalMediaRoleDecision.Assigned, "agent", Now));
            overrides.Add(new(Before2, VerticalMediaRole.BeforeRenovation, VerticalMediaRoleDecision.Assigned, "agent", Now));
            overrides.Add(new(After1, VerticalMediaRole.AfterRenovation, VerticalMediaRoleDecision.Assigned, "agent", Now));
            overrides.Add(new(After2, VerticalMediaRole.AfterRenovation, VerticalMediaRoleDecision.Assigned, "agent", Now));
            overrides.Add(new(IntroVideo, VerticalMediaRole.AgentIntro, VerticalMediaRoleDecision.Assigned, "agent", Now));
            overrides.Add(new(WalkVideo, VerticalMediaRole.HostedWalkthrough, VerticalMediaRoleDecision.Assigned, "agent", Now));
        }

        var suggested = VerticalMediaRoles.Suggest(rolePhotos, footage);
        var lookup = overrides.ToLookup(item => item.TargetId);
        IReadOnlyList<VerticalMediaRoleState> Roles(Guid id) =>
            VerticalMediaRoles.Resolve(suggested.TryGetValue(id, out var roles) ? roles : null, lookup[id]);

        return new VerticalPlanningInput(
            Id(100),
            Facts(),
            stops.Select((stop, index) => new VerticalPlanningPhoto(stop.Id, stop.Stop, stop.Category, true, stop.Quality,
                stop.Hero, stop.Exterior, 4032, 3024, index, Roles(stop.Id))).ToArray(),
            footage.Select((video, index) => new VerticalPlanningFootage(video.VideoId, video.DurationMs, video.IsReady,
                video.HasAudio, video.TourStop, index, Roles(video.VideoId))).ToArray(),
            [],
            VerticalPresenterInput.None,
            null,
            null,
            null,
            VerticalUnconfirmedMentions.None);
    }

    /// <summary>Every first-release input confirmed: hero, before/after, intro and walk-through clips, narration, story.</summary>
    public static VerticalPlanningInput Rich() => Input(rich: true) with
    {
        Narration = Narration(),
        VerifiedPriceChange = 10_000,
        StoryClaim = new VerticalStoryClaimInput(Id(50), "The kitchen was closed off and dark.", "Now it opens to the living room and the yard."),
    };

    private static Guid Id(int index) => new($"00000000-0000-0000-0000-{index:000000000000}");
}
