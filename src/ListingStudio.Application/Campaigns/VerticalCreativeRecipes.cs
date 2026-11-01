using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Properties;

namespace ListingStudio.Application.Campaigns;

public enum VerticalBeatKind
{
    Hook,
    FeatureDetail,
    Context,
    LoopReturn,
    PriceReveal,
    EssentialFacts,
    ValueMessage,
    RoomTour,
    PriceRecap,
    HostIntro,
    HostedWalk,
    HostOutro,
    Problem,
    Tension,
    Result,
    Payoff,
    AccessHook,
    InsideLook,
    AccessDetails,
    CallToAction,
}

public enum VerticalVisualTreatment
{
    Photo,
    Footage,

    /// <summary>The consented presenter, rendered from the agent's portrait.</summary>
    Presenter,

    /// <summary>Text on a softened photo, used when no photo shows what the line says.</summary>
    TextCardOverPhoto,
}

/// <summary>How a format's voice is built. Each first-release format has its own.</summary>
public enum VerticalNarrationStructure
{
    /// <summary>Music and on-screen text only.</summary>
    MusicOnly,

    /// <summary>Short spoken read of the verified facts.</summary>
    FactsReadout,

    /// <summary>The host's own words: approved narration, or the footage's own sound.</summary>
    FirstPersonHost,

    /// <summary>The agent's confirmed problem, then the result and payoff.</summary>
    StoryArc,

    /// <summary>The agent's confirmed access language, then the inside look.</summary>
    AccessAnnouncement,
}

/// <summary>How much text sits on screen. Each first-release format has its own.</summary>
public enum VerticalCaptionDensity
{
    /// <summary>A hook and a call to action, nothing else.</summary>
    Minimal,

    /// <summary>Full fact cards: price, place, beds and baths.</summary>
    FactCards,

    /// <summary>Word-by-word captions of the host's voice.</summary>
    WordTimed,

    /// <summary>One short phrase per story beat.</summary>
    PhraseCards,
}

/// <summary>Where a line of copy comes from. Prompt 6 maps these to the video's fact bindings.</summary>
public enum VerticalCopySource
{
    VerifiedProperty,
    AgentNarration,
    AgentClaim,
    AgentCampaignInput,
    AgentStoryboard,
    MediaAnalysis,

    /// <summary>Fixed, claim-free copy from the format itself, such as a call to action.</summary>
    ApprovedTemplate,

    /// <summary>A sentence of the generated property story, which already passed the story's grounding rules.</summary>
    PropertyStory,
}

public sealed record VerticalRecipeFact(string Key, string Value, VerticalCopySource Source, string Reference);

public sealed record VerticalRecipeLine(string Text, string GroundingKey);

public sealed record VerticalRecipeBeat(
    int Order,
    VerticalBeatKind Kind,
    VerticalVisualTreatment Treatment,
    Guid? PropertyMediaId,
    Guid? PropertyVideoId,
    int StartMs,
    int DurationMs,
    VerticalRecipeLine? Overlay,
    VerticalRecipeLine? Narration,
    bool KeepsSourceAudio = false,
    int SourceOffsetMs = 0);

/// <summary>
/// One format's complete, fixed plan: scene order, hook, voice structure, caption density, call to action and purpose.
/// Every line on screen or in the voice names a grounding key listed in <see cref="Facts"/>.
/// </summary>
public sealed record VerticalCreativeRecipe(
    string RecipeVersion,
    VerticalCreativeFormat Format,
    VerticalCampaignPurpose Purpose,
    int DurationSeconds,
    bool Loops,
    VerticalRecipeLine Hook,
    VerticalNarrationStructure Narration,
    VerticalCaptionDensity CaptionDensity,
    int MaximumCaptionWords,
    VerticalRecipeLine CallToAction,
    Guid? PrimaryPropertyMediaId,
    Guid? PrimaryPropertyVideoId,
    bool UsesPresenter,
    IReadOnlyList<VerticalRecipeBeat> Beats,
    IReadOnlyList<VerticalRecipeFact> Facts,
    IReadOnlyList<string> Fallbacks)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    private static readonly JsonSerializerOptions IndentedOptions = new(JsonOptions) { WriteIndented = true };

    public string ToIndentedJson() => JsonSerializer.Serialize(this, IndentedOptions);

    public static VerticalCreativeRecipe? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<VerticalCreativeRecipe>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public CampaignVideoVariantPlan ToVariantPlan(int sequence, string hookVariant = CampaignVideoVariant.DefaultHookVariant) => new(
        Format,
        Purpose,
        sequence,
        DurationSeconds,
        true,
        Hook.Text,
        Hook.GroundingKey,
        PrimaryPropertyMediaId,
        Narration switch
        {
            VerticalNarrationStructure.MusicOnly => CampaignVoiceoverMode.None,
            _ => CampaignVoiceoverMode.ShortHook,
        },
        CallToAction.Text,
        ToJson(),
        RecipeVersion,
        HookVariant: hookVariant);

    /// <summary>Problems with the recipe; empty when it is complete, contiguous and fully grounded.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        var keys = Facts.Select(fact => fact.Key).ToHashSet(StringComparer.Ordinal);
        if (Facts.Count != keys.Count)
        {
            problems.Add("A grounding key is listed twice.");
        }

        if (!CampaignVideoVariant.AllowedDurations.Contains(DurationSeconds))
        {
            problems.Add($"Duration {DurationSeconds} s is not allowed.");
        }

        if (Hook.Text.Length > CampaignVideoVariant.MaximumHookLength || string.IsNullOrWhiteSpace(Hook.Text))
        {
            problems.Add("The hook is empty or too long.");
        }

        if (CallToAction.Text.Length > CampaignVideoVariant.MaximumCallToActionLength || string.IsNullOrWhiteSpace(CallToAction.Text))
        {
            problems.Add("The call to action is empty or too long.");
        }

        foreach (var line in Beats.SelectMany(beat => new[] { beat.Overlay, beat.Narration }).Append(Hook).Append(CallToAction))
        {
            if (line is not null && !keys.Contains(line.GroundingKey))
            {
                problems.Add($"Line grounded on unknown key {line.GroundingKey}.");
            }
        }

        var cursor = 0;
        foreach (var beat in Beats.OrderBy(beat => beat.Order))
        {
            if (beat.StartMs != cursor || beat.DurationMs <= 0)
            {
                problems.Add($"Beat {beat.Order} does not follow the previous one.");
            }

            if (beat.PropertyMediaId is null && beat.PropertyVideoId is null && beat.Treatment != VerticalVisualTreatment.Presenter)
            {
                problems.Add($"Beat {beat.Order} has nothing to show.");
            }

            cursor = beat.StartMs + beat.DurationMs;
        }

        if (cursor != DurationSeconds * 1000)
        {
            problems.Add($"Beats last {cursor} ms, not {DurationSeconds * 1000} ms.");
        }

        if (Loops && (Beats.Count < 2 || Beats[^1].PropertyMediaId != Beats[0].PropertyMediaId
            || Beats[^1].PropertyVideoId != Beats[0].PropertyVideoId))
        {
            problems.Add("A looping video must end on its opening shot.");
        }

        return problems;
    }
}

/// <summary>Plans one format's recipe from the listing's planning input.</summary>
public interface IVerticalFormatPlanner
{
    VerticalCreativeFormat Format { get; }

    string RecipeVersion { get; }

    /// <summary>The recipe, or null when the listing lacks what the format strictly needs.</summary>
    VerticalCreativeRecipe? Plan(VerticalPlanningInput input);
}

/// <summary>Runs the planner of every eligible format.</summary>
public static class VerticalRecipePlanner
{
    public static IReadOnlyList<IVerticalFormatPlanner> Planners { get; } =
    [
        new WowFeaturePlanner(),
        new PriceFirstPlanner(),
        new HostedPovPlanner(),
        new StoryRevealPlanner(),
        new ExclusiveAccessPlanner(),
    ];

    public static IVerticalFormatPlanner For(VerticalCreativeFormat format) =>
        Planners.Single(planner => planner.Format == format);

    /// <summary>Recipes for the given formats, in the order given; formats whose planner declines are left out.</summary>
    public static IReadOnlyList<VerticalCreativeRecipe> Plan(
        VerticalPlanningInput input,
        IEnumerable<VerticalCreativeFormat> formats) =>
        formats.Select(format => For(format).Plan(input)).OfType<VerticalCreativeRecipe>().ToArray();

    /// <summary>Recipes for every format the recommendation found eligible, in rank order.</summary>
    public static IReadOnlyList<VerticalCreativeRecipe> PlanEligible(
        VerticalPlanningInput input,
        VerticalFormatRecommendationSet recommendation) =>
        Plan(input, recommendation.AllEligible.Select(item => item.Format));
}

/// <summary>Shared helpers: grounded facts, photo choice and timing.</summary>
internal sealed class RecipeBuilder(VerticalCreativeFormat format, string version)
{
    private static readonly CultureInfo UnitedStates = CultureInfo.GetCultureInfo("en-US");

    private readonly Dictionary<string, VerticalRecipeFact> facts = new(StringComparer.Ordinal);
    private readonly List<VerticalRecipeBeat> beats = [];
    private readonly List<string> fallbacks = [];
    private int cursor;

    public VerticalRecipeLine Line(string key, string text, VerticalCopySource source, string reference)
    {
        facts.TryAdd(key, new VerticalRecipeFact(key, text, source, reference));
        return new VerticalRecipeLine(text, key);
    }

    public VerticalRecipeLine Template(string key, string text) =>
        Line($"template.{key}", text, VerticalCopySource.ApprovedTemplate, $"{version} template");

    public void Fallback(string reason) => fallbacks.Add(reason);

    public void Beat(
        VerticalBeatKind kind,
        int durationMs,
        VerticalPlanningPhoto? photo = null,
        VerticalPlanningFootage? footage = null,
        VerticalRecipeLine? overlay = null,
        VerticalRecipeLine? narration = null,
        VerticalVisualTreatment? treatment = null,
        int sourceOffsetMs = 0)
    {
        var visual = treatment ?? (footage is not null ? VerticalVisualTreatment.Footage : VerticalVisualTreatment.Photo);
        beats.Add(new VerticalRecipeBeat(
            beats.Count + 1,
            kind,
            visual,
            footage is null ? photo?.MediaId : null,
            footage?.VideoId,
            cursor,
            durationMs,
            overlay,
            narration,
            footage is { HasAudio: true } && visual == VerticalVisualTreatment.Footage && narration is null,
            footage is null ? 0 : sourceOffsetMs));
        cursor += durationMs;
    }

    public int Elapsed => cursor;

    public VerticalRecipeLine? Price(VerticalListingFacts listing) => listing.PriceText is { } price
        ? Line("property.listingPrice", price, VerticalCopySource.VerifiedProperty, "ListingPrice")
        : null;

    public VerticalRecipeLine? PriceAndPlace(VerticalListingFacts listing) =>
        listing.PriceText is { } price && listing.CityStateText is { } city
            ? Line("property.priceCityState", $"{price} in {city}", VerticalCopySource.VerifiedProperty, "ListingPrice+City+State")
            : null;

    public VerticalRecipeLine? CityState(VerticalListingFacts listing) => listing.CityStateText is { } city
        ? Line("property.cityState", city, VerticalCopySource.VerifiedProperty, "City+State")
        : null;

    public VerticalRecipeLine? EssentialFacts(VerticalListingFacts listing) => listing.EssentialFactsText is { } essentials
        ? Line("property.essentialFacts", essentials, VerticalCopySource.VerifiedProperty, "Bedrooms+Bathrooms+SquareFeet")
        : null;

    public VerticalRecipeLine? PriceChange(decimal? amount) => amount > 0
        ? Line("campaign.priceReduction", $"Price reduced {amount.Value.ToString("C0", UnitedStates)}",
            VerticalCopySource.AgentCampaignInput, "Agent-confirmed price change")
        : null;

    /// <summary>The photo's room for a caption: the agent's storyboard stop first, else the analysed category.</summary>
    public VerticalRecipeLine? Room(VerticalPlanningPhoto photo)
    {
        if (!string.IsNullOrWhiteSpace(photo.Stop))
        {
            return Line($"media.{photo.MediaId:N}.stop", photo.Stop.Trim(), VerticalCopySource.AgentStoryboard, "WalkthroughStop");
        }

        return RoomName(photo.Category) is { } name
            ? Line($"media.{photo.MediaId:N}.category", Capitalize(name), VerticalCopySource.MediaAnalysis, "Category")
            : null;
    }

    public VerticalCreativeRecipe Build(
        int durationSeconds,
        bool loops,
        VerticalRecipeLine hook,
        VerticalNarrationStructure narration,
        VerticalCaptionDensity density,
        int maximumCaptionWords,
        VerticalRecipeLine callToAction,
        Guid? primaryMedia,
        Guid? primaryVideo,
        bool usesPresenter)
    {
        var recipe = new VerticalCreativeRecipe(
            version,
            format,
            VerticalFormatRules.PurposeOf(format),
            durationSeconds,
            loops,
            hook,
            narration,
            density,
            maximumCaptionWords,
            callToAction,
            primaryMedia,
            primaryVideo,
            usesPresenter,
            beats.ToArray(),
            facts.Values.OrderBy(fact => fact.Key, StringComparer.Ordinal).ToArray(),
            fallbacks.ToArray());
        var problems = recipe.Validate();
        if (problems.Count > 0)
        {
            throw new InvalidOperationException($"The {format} recipe is invalid: {string.Join(" ", problems)}");
        }

        return recipe;
    }

    /// <summary>Splits a span into near-equal whole-millisecond parts that add up exactly.</summary>
    public static int[] Split(int totalMs, int parts)
    {
        if (parts <= 0)
        {
            return [];
        }

        var each = totalMs / parts;
        var result = Enumerable.Repeat(each, parts).ToArray();
        result[^1] += totalMs - (each * parts);
        return result;
    }

    public static string? RoomName(PropertyMediaCategory? category) => category switch
    {
        PropertyMediaCategory.FrontExterior => "front exterior",
        PropertyMediaCategory.RearExterior => "back of the home",
        PropertyMediaCategory.Aerial => "view from above",
        PropertyMediaCategory.Entry => "entry",
        PropertyMediaCategory.LivingRoom => "living room",
        PropertyMediaCategory.Kitchen => "kitchen",
        PropertyMediaCategory.DiningRoom => "dining room",
        PropertyMediaCategory.PrimaryBedroom => "primary bedroom",
        PropertyMediaCategory.Bedroom => "bedroom",
        PropertyMediaCategory.PrimaryBathroom => "primary bathroom",
        PropertyMediaCategory.Bathroom => "bathroom",
        PropertyMediaCategory.Office => "office",
        PropertyMediaCategory.Laundry => "laundry room",
        PropertyMediaCategory.Garage => "garage",
        PropertyMediaCategory.Pool => "pool",
        PropertyMediaCategory.Patio => "patio",
        PropertyMediaCategory.Backyard => "backyard",
        PropertyMediaCategory.Community => "community",
        _ => null,
    };

    private static string Capitalize(string text) => char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>A photo of the outside of the home or its grounds, by the analysis.</summary>
    public static bool IsOutside(VerticalPlanningPhoto photo) =>
        photo.IsExterior == true || photo.Category is PropertyMediaCategory.FrontExterior or PropertyMediaCategory.RearExterior
            or PropertyMediaCategory.Aerial or PropertyMediaCategory.Patio or PropertyMediaCategory.Backyard
            or PropertyMediaCategory.Pool or PropertyMediaCategory.Community or PropertyMediaCategory.Garage;

    /// <summary>A spoken line as a grounded recipe line: the agent's narration, or a sentence of the generated story.</summary>
    public VerticalRecipeLine Spoken(VerticalNarrationLine line) => line.FromAgent
        ? Line(line.GroundingKey, line.Text, VerticalCopySource.AgentNarration, "Approved narration")
        : Line(line.GroundingKey, line.Text, VerticalCopySource.PropertyStory, "Story voiceover");

    /// <summary>About how long a line takes to say at a relaxed social pace (2.5 words a second), plus a breath.</summary>
    public static int SpeakingMs(string text) =>
        (text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length * 400) + 250;

    /// <summary>
    /// Room-by-room shots with the voice running through them: each line starts on the next shot after the previous
    /// line has finished, waits for the shot of its own storyboard stop when the agent's narration names one, and is
    /// left out when it would still be talking at <paramref name="mustEndByMs"/>. Shots are at most
    /// <paramref name="maximumShotMs"/> long so nothing lingers.
    /// </summary>
    private const int LongestShotMs = 4_000;

    public void NarratedWalk(
        VerticalBeatKind kind,
        int totalMs,
        IReadOnlyList<VerticalPlanningPhoto> photos,
        IReadOnlyList<VerticalNarrationLine> lines,
        int mustEndByMs,
        Func<VerticalPlanningPhoto, VerticalRecipeLine?>? overlay = null,
        int maximumShotMs = 2_800)
    {
        if (photos.Count == 0 || totalMs <= 0)
        {
            return;
        }

        var count = Math.Clamp((totalMs + maximumShotMs - 1) / maximumShotMs, 1, photos.Count);
        var shots = photos.Take(count).ToArray();
        if (totalMs / count > LongestShotMs)
        {
            // Far fewer photos than the span needs: cycle through them again rather than hold one for five seconds.
            count = (totalMs + maximumShotMs - 1) / maximumShotMs;
            shots = Enumerable.Range(0, count).Select(index => photos[index % photos.Count]).ToArray();
        }

        var spans = Split(totalMs, shots.Length);
        var queue = new Queue<VerticalNarrationLine>(lines);
        var freeAtMs = cursor;
        for (var index = 0; index < shots.Length; index++)
        {
            var startMs = cursor;
            VerticalRecipeLine? spoken = null;
            while (queue.Count > 0 && startMs >= freeAtMs)
            {
                var next = queue.Peek();
                var endsAt = startMs + SpeakingMs(next.Text);
                if (endsAt > mustEndByMs)
                {
                    queue.Dequeue();
                    continue;
                }

                var waitsForStop = next.Stop is { } stop
                    && !string.Equals(shots[index].Stop?.Trim(), stop.Trim(), StringComparison.OrdinalIgnoreCase)
                    && shots.Skip(index + 1).Any(shot => string.Equals(shot.Stop?.Trim(), stop.Trim(), StringComparison.OrdinalIgnoreCase));
                if (waitsForStop)
                {
                    break;
                }

                spoken = Spoken(queue.Dequeue());
                freeAtMs = endsAt;
                break;
            }

            Beat(kind, spans[index], shots[index], overlay: overlay?.Invoke(shots[index]), narration: spoken);
        }
    }

    /// <summary>Photos with a role, the agent's choices first, then in walking order.</summary>
    public static IEnumerable<VerticalPlanningPhoto> WithRole(IEnumerable<VerticalPlanningPhoto> photos, VerticalMediaRole role) =>
        photos.Where(photo => photo.Has(role))
            .OrderByDescending(photo => photo.HasFromAgent(role))
            .ThenBy(photo => photo.DisplayOrder)
            .ThenBy(photo => photo.MediaId);

    public static IEnumerable<VerticalPlanningPhoto> ByStrength(IEnumerable<VerticalPlanningPhoto> photos) =>
        photos.OrderByDescending(photo => photo.HeroScore ?? 0)
            .ThenByDescending(photo => photo.QualityScore ?? 0)
            .ThenBy(photo => photo.DisplayOrder)
            .ThenBy(photo => photo.MediaId);
}

/// <summary>
/// Wow Feature: opens on the single most striking feature, cuts through its details, and returns to the same shot so
/// the 15-second video loops. Music and a few words only.
/// </summary>
public sealed class WowFeaturePlanner : IVerticalFormatPlanner
{
    public VerticalCreativeFormat Format => VerticalCreativeFormat.WowFeature;

    public string RecipeVersion => "wow-feature-v2";

    public VerticalCreativeRecipe? Plan(VerticalPlanningInput input)
    {
        var builder = new RecipeBuilder(Format, RecipeVersion);
        var usable = input.CurrentPhotos;
        var primary = RecipeBuilder.WithRole(usable, VerticalMediaRole.HeroFeature).FirstOrDefault();
        if (primary is null)
        {
            primary = RecipeBuilder.ByStrength(usable.Where(photo => photo.AnalysisCompleted
                    && photo.HeroScore >= 70 && photo.QualityScore >= 60 && VerticalMediaRoles.IsFeatureCategory(photo.Category)))
                .FirstOrDefault();
            if (primary is null)
            {
                return null;
            }

            builder.Fallback("No hero feature is tagged, so the highest-scoring feature photo opens the video.");
        }

        var room = RecipeBuilder.RoomName(primary.Category) ?? primary.Stop?.Trim().ToLowerInvariant();
        var hook = room is null
            ? builder.Template("hook.wow", "Look at this.")
            : builder.Line($"media.{primary.MediaId:N}.feature", $"Look at this {room}.",
                primary.Category is null ? VerticalCopySource.AgentStoryboard : VerticalCopySource.MediaAnalysis,
                "Hero feature room");
        // The hook promised a room, so the details stay indoors when that room is indoors: cutting from "Look at this
        // living room" to the side of the house breaks the promise in the first two seconds.
        var indoorHook = !RecipeBuilder.IsOutside(primary);
        var details = RecipeBuilder.WithRole(
                usable.Where(photo => photo != primary && !(indoorHook && RecipeBuilder.IsOutside(photo))),
                VerticalMediaRole.FeatureDetail)
            .OrderByDescending(photo => photo.Category == primary.Category && primary.Category is not null)
            .Take(3)
            .ToList();
        if (details.Count < 3)
        {
            var extra = RecipeBuilder.ByStrength(usable.Where(photo => photo != primary && !details.Contains(photo)
                    && !(indoorHook && RecipeBuilder.IsOutside(photo))))
                .Take(3 - details.Count)
                .ToArray();
            if (extra.Length > 0)
            {
                builder.Fallback($"{extra.Length} detail shot{(extra.Length == 1 ? string.Empty : "s")} filled from the strongest other photos.");
            }

            details.AddRange(extra);
        }

        var callToAction = builder.Template("cta.save", "Save this for later");
        var city = builder.CityState(input.Facts);
        var middle = details.Count + (city is null ? 0 : 1);
        var spans = RecipeBuilder.Split(15_000 - 3_000 - 2_500, Math.Max(middle, 1));
        builder.Beat(VerticalBeatKind.Hook, 3_000, primary, overlay: hook);
        if (middle == 0)
        {
            builder.Fallback("No other usable photo, so the feature holds for the middle of the loop.");
            builder.Beat(VerticalBeatKind.FeatureDetail, spans[0], primary);
        }

        for (var index = 0; index < details.Count; index++)
        {
            builder.Beat(VerticalBeatKind.FeatureDetail, spans[index], details[index]);
        }

        if (city is not null)
        {
            var context = usable.FirstOrDefault(photo => photo != primary && !details.Contains(photo)
                    && (photo.Has(VerticalMediaRole.Lifestyle) || photo.Has(VerticalMediaRole.Exterior)))
                ?? usable.FirstOrDefault(photo => photo != primary && !details.Contains(photo))
                ?? (details.Count > 0 ? details[^1] : primary);
            builder.Beat(VerticalBeatKind.Context, spans[^1], context, overlay: city);
        }

        builder.Beat(VerticalBeatKind.LoopReturn, 2_500, primary, overlay: callToAction);
        return builder.Build(15, true, hook, VerticalNarrationStructure.MusicOnly, VerticalCaptionDensity.Minimal, 4,
            callToAction, primary.MediaId, null, false);
    }
}

/// <summary>
/// Price First: leads with the verified price and city, then the essential facts, a quick room tour in walking order
/// and a price recap. About 30 seconds with a short spoken read of the facts.
/// </summary>
public sealed class PriceFirstPlanner : IVerticalFormatPlanner
{
    public VerticalCreativeFormat Format => VerticalCreativeFormat.PriceFirst;

    public string RecipeVersion => "price-first-v2";

    public VerticalCreativeRecipe? Plan(VerticalPlanningInput input)
    {
        var builder = new RecipeBuilder(Format, RecipeVersion);
        var hook = builder.PriceAndPlace(input.Facts);
        var essentials = builder.EssentialFacts(input.Facts);
        var usable = input.CurrentPhotos;
        if (hook is null || essentials is null || usable.Count < 3)
        {
            return null;
        }

        var primary = usable
            .Where(photo => photo.Has(VerticalMediaRole.Exterior))
            .OrderByDescending(photo => photo.Category == PropertyMediaCategory.FrontExterior
                || photo.Stop?.Contains("front", StringComparison.OrdinalIgnoreCase) == true)
            .ThenByDescending(photo => photo.HasFromAgent(VerticalMediaRole.Exterior))
            .ThenByDescending(photo => photo.HeroScore ?? 0)
            .ThenBy(photo => photo.DisplayOrder)
            .FirstOrDefault();
        if (primary is null)
        {
            primary = usable[0];
            builder.Fallback("No exterior photo is tagged, so the first storyboard photo carries the price.");
        }

        var tour = usable.Where(photo => photo != primary).ToList();
        var factsPhoto = tour[0];
        tour.RemoveAt(0);
        var priceChange = builder.PriceChange(input.VerifiedPriceChange);
        var callToAction = builder.Template("cta.details", "Ask for the full listing details");
        var recap = builder.Price(input.Facts)!;

        // End inside, on the home's best room, rather than back on the house the video opened on; the agent's own
        // closing shot still wins.
        var closing = RecipeBuilder.WithRole(usable, VerticalMediaRole.ClosingView)
                .FirstOrDefault(photo => photo != primary && photo.HasFromAgent(VerticalMediaRole.ClosingView))
            ?? RecipeBuilder.ByStrength(usable.Where(photo => photo != primary && !RecipeBuilder.IsOutside(photo)
                && (photo.HeroScore ?? 0) >= 60)).FirstOrDefault()
            ?? primary;

        builder.Beat(VerticalBeatKind.PriceReveal, 3_500, primary, overlay: hook, narration: hook);
        builder.Beat(VerticalBeatKind.EssentialFacts, 3_500, factsPhoto, overlay: essentials, narration: essentials);
        if (priceChange is not null)
        {
            var valuePhoto = tour.Count > 0 ? tour[0] : factsPhoto;
            if (tour.Count > 0)
            {
                tour.RemoveAt(0);
            }

            builder.Beat(VerticalBeatKind.ValueMessage, 3_000, valuePhoto, overlay: priceChange, narration: priceChange);
        }

        const int RecapMs = 4_000;
        var tourMs = 30_000 - builder.Elapsed - RecapMs;
        tour.Remove(closing);
        if (tour.Count == 0)
        {
            builder.Fallback("Few photos, so the facts photo repeats in the room tour.");
            tour.Add(factsPhoto);
        }

        // The voice keeps going through the room tour, one line per room, instead of leaving it to the music.
        builder.NarratedWalk(VerticalBeatKind.RoomTour, tourMs, tour, input.SpokenLines, 30_000 - RecapMs - 200,
            builder.Room);
        builder.Beat(VerticalBeatKind.PriceRecap, RecapMs, closing, overlay: recap, narration: callToAction);
        // With a consented presenter, the agent talks the viewer through the price and the rooms.
        return builder.Build(30, false, hook, VerticalNarrationStructure.FactsReadout, VerticalCaptionDensity.FactCards, 8,
            callToAction, primary.MediaId, null, input.Presenter.IsConsented);
    }
}

/// <summary>
/// Hosted POV: opens on the host (intro footage or the consented presenter) with a first-person line in the agent's
/// own words, walks the home with the host's voice, and closes on the host. About 30 seconds, word-timed captions.
/// </summary>
public sealed class HostedPovPlanner : IVerticalFormatPlanner
{
    private const int IntroMs = 4_000;
    private const int OutroMs = 4_000;

    public VerticalCreativeFormat Format => VerticalCreativeFormat.HostedPov;

    public string RecipeVersion => "hosted-pov-v2";

    public VerticalCreativeRecipe? Plan(VerticalPlanningInput input)
    {
        var builder = new RecipeBuilder(Format, RecipeVersion);
        var usable = input.CurrentPhotos;

        // Only footage the agent tagged: a suggested tag just means a long video with sound, which is true of any
        // phone walk-through, and planning that as "the agent talking" gave a silent tour of a garage door.
        var host = input.Footage.Where(video => video.IsConfirmedHostFootage)
            .OrderBy(video => video.TourOrder).ThenBy(video => video.VideoId).ToArray();
        var intro = host.Where(video => video.HasFromAgent(VerticalMediaRole.AgentIntro)).FirstOrDefault();
        var walks = host.Where(video => video != intro
            && (video.HasFromAgent(VerticalMediaRole.HostedWalkthrough) || video.HasFromAgent(VerticalMediaRole.LiveReplay))).ToArray();
        var hasWalkFootage = walks.Sum(video => (long)video.DurationMs) >= 15_000;
        var presenter = input.Presenter.IsConsented;
        var lines = input.SpokenLines;
        if (!hasWalkFootage && intro is null && input.Narration.Count == 0 && !presenter)
        {
            return null;
        }

        if (!hasWalkFootage && usable.Count < 3)
        {
            return null;
        }

        var hook = FirstPersonHook(builder, lines);
        var callToAction = builder.Template("cta.showing", "Message me to see it in person");
        var hookUsesLine = lines.Count > 0 && hook.GroundingKey == lines[0].GroundingKey;
        var walkLines = hookUsesLine ? lines.Skip(1).ToArray() : lines.ToArray();

        // The presenter beats show a photo underneath when the presenter cannot be drawn, so there always is one.
        Guid? primaryVideo = null;
        Guid? primaryMedia = null;
        var walkPhotos = usable.ToList();

        // Opening: the agent's own intro clip (their voice), the presenter saying the hook, or the hook voiced over
        // the first room.
        if (intro is not null)
        {
            builder.Beat(VerticalBeatKind.HostIntro, IntroMs, footage: intro, overlay: hook);
            primaryVideo = intro.VideoId;
        }
        else if (presenter)
        {
            builder.Beat(VerticalBeatKind.HostIntro, IntroMs, usable.Count > 0 ? usable[0] : null, overlay: hook,
                narration: hook, treatment: VerticalVisualTreatment.Presenter);
        }
        else if (hasWalkFootage)
        {
            builder.Fallback("No intro clip or presenter, so the hosted walk-through opens the video.");
            builder.Beat(VerticalBeatKind.HostIntro, IntroMs, footage: walks[0], overlay: hook);
            primaryVideo = walks[0].VideoId;
        }
        else
        {
            builder.Fallback("No host on camera, so the host's voice opens over the first storyboard photo.");
            builder.Beat(VerticalBeatKind.HostIntro, IntroMs, usable[0], overlay: hook, narration: hook);
            walkPhotos.RemoveAt(0);
        }

        var walkMs = 30_000 - IntroMs - OutroMs;
        if (hasWalkFootage)
        {
            // The agent's own walk-through keeps its own sound: they are the voice.
            var spans = RecipeBuilder.Split(walkMs, walks.Length);
            var openedOnWalk = intro is null && !presenter;
            for (var index = 0; index < walks.Length; index++)
            {
                builder.Beat(VerticalBeatKind.HostedWalk, spans[index], footage: walks[index],
                    sourceOffsetMs: index == 0 && openedOnWalk ? IntroMs : 0);
            }

            primaryVideo ??= walks[0].VideoId;
        }
        else
        {
            if (walkPhotos.Count == 0)
            {
                walkPhotos.AddRange(usable);
            }

            // The host talks the whole way through, one line per room, a new shot every two to three seconds.
            builder.NarratedWalk(VerticalBeatKind.HostedWalk, walkMs, walkPhotos, walkLines, 30_000 - OutroMs - 200);
        }

        if (presenter)
        {
            builder.Beat(VerticalBeatKind.HostOutro, OutroMs, usable.Count > 0 ? usable[0] : null, overlay: callToAction,
                narration: callToAction, treatment: VerticalVisualTreatment.Presenter);
        }
        else
        {
            var closing = RecipeBuilder.WithRole(usable, VerticalMediaRole.ClosingView).FirstOrDefault() ?? (usable.Count > 0 ? usable[0] : null);
            if (closing is null)
            {
                builder.Beat(VerticalBeatKind.HostOutro, OutroMs, footage: walks.Length > 0 ? walks[^1] : intro, overlay: callToAction);
            }
            else
            {
                builder.Beat(VerticalBeatKind.HostOutro, OutroMs, closing, overlay: callToAction, narration: callToAction);
            }
        }

        if ((primaryVideo is null || presenter) && usable.Count > 0)
        {
            primaryMedia = usable[0].MediaId;
        }

        return builder.Build(30, false, hook, VerticalNarrationStructure.FirstPersonHost, VerticalCaptionDensity.WordTimed, 3,
            callToAction, primaryMedia, primaryVideo, presenter);
    }

    /// <summary>The first sentence of the agent's approved narration when it fits as a hook, else a claim-free line.</summary>
    private static VerticalRecipeLine FirstPersonHook(RecipeBuilder builder, IReadOnlyList<VerticalNarrationLine> lines)
    {
        // Only the agent's own words make a first-person hook; a story sentence is a description, not a greeting.
        if (lines.Count > 0 && lines[0].FromAgent)
        {
            var text = lines[0].Text.Trim();
            var end = text.IndexOfAny(['.', '!', '?']);
            var sentence = end > 0 && end == text.Length - 1 ? text : null;
            if (sentence is { Length: > 0 and <= CampaignVideoVariant.MaximumHookLength })
            {
                return builder.Line(lines[0].GroundingKey, sentence, VerticalCopySource.AgentNarration, "Approved narration");
            }
        }

        return builder.Template("hook.hostedPov", "Come walk through this one with me.");
    }
}

/// <summary>
/// Story Reveal: the agent's confirmed problem over the before photos, tension, the result over the after photos, then
/// the payoff. 45 seconds with at least two before and two after photos, otherwise 30.
/// </summary>
public sealed class StoryRevealPlanner : IVerticalFormatPlanner
{
    public VerticalCreativeFormat Format => VerticalCreativeFormat.StoryReveal;

    public string RecipeVersion => "story-reveal-v1";

    public VerticalCreativeRecipe? Plan(VerticalPlanningInput input)
    {
        var claim = input.StoryClaim;
        var usable = input.UsablePhotos;
        if (claim is null || usable.Count < 3)
        {
            return null;
        }

        var builder = new RecipeBuilder(Format, RecipeVersion);
        var before = usable.Where(photo => photo.HasFromAgent(VerticalMediaRole.BeforeRenovation)).ToList();
        var after = usable.Where(photo => photo.HasFromAgent(VerticalMediaRole.AfterRenovation)).ToList();
        var longForm = before.Count >= 2 && after.Count >= 2;
        var seconds = longForm ? 45 : 30;
        int[] plan = longForm ? [6_000, 9_000, 17_000, 8_000, 5_000] : [5_000, 5_000, 10_000, 6_000, 4_000];
        var problem = builder.Line("claim.story.problem", claim.Problem, VerticalCopySource.AgentClaim, $"ListingCreativeClaim:{claim.ClaimId:D}");
        var payoff = builder.Line("claim.story.payoff", claim.Payoff, VerticalCopySource.AgentClaim, $"ListingCreativeClaim:{claim.ClaimId:D}");
        var callToAction = builder.Template("cta.story", "Message me for the full story");
        var others = RecipeBuilder.ByStrength(usable.Where(photo => !before.Contains(photo) && !after.Contains(photo))).ToList();

        // Problem and tension: the before photos. Without them the problem is a text card, never a photo that may not
        // show it.
        if (before.Count == 0)
        {
            builder.Fallback("No before photos are tagged, so the problem is shown as a text card.");
            builder.Beat(VerticalBeatKind.Problem, plan[0], usable[0], overlay: problem, narration: problem,
                treatment: VerticalVisualTreatment.TextCardOverPhoto);
            builder.Beat(VerticalBeatKind.Tension, plan[1], usable[0], treatment: VerticalVisualTreatment.TextCardOverPhoto);
        }
        else
        {
            builder.Beat(VerticalBeatKind.Problem, plan[0], before[0], overlay: problem, narration: problem);
            var tension = before.Skip(1).DefaultIfEmpty(before[0]).Take(longForm ? 2 : 1).ToArray();
            var spans = RecipeBuilder.Split(plan[1], tension.Length);
            for (var index = 0; index < tension.Length; index++)
            {
                builder.Beat(VerticalBeatKind.Tension, spans[index], tension[index]);
            }
        }

        // Result: the after photos, then the strongest other photos.
        if (after.Count == 0)
        {
            builder.Fallback("No after photos are tagged, so the strongest current photos show the result.");
        }

        var result = after.Concat(others).Take(longForm ? 4 : 3).ToArray();
        var resultSpans = RecipeBuilder.Split(plan[2], result.Length);
        for (var index = 0; index < result.Length; index++)
        {
            builder.Beat(VerticalBeatKind.Result, resultSpans[index], result[index]);
        }

        var payoffPhoto = after.Count > 1 ? after[^1] : result[0];
        builder.Beat(VerticalBeatKind.Payoff, plan[3], payoffPhoto, overlay: payoff, narration: payoff);
        var closing = RecipeBuilder.WithRole(usable, VerticalMediaRole.ClosingView).FirstOrDefault() ?? result[0];
        builder.Beat(VerticalBeatKind.CallToAction, plan[4], closing, overlay: callToAction);
        return builder.Build(seconds, false, problem, VerticalNarrationStructure.StoryArc, VerticalCaptionDensity.PhraseCards, 6,
            callToAction, result[0].MediaId, null, false);
    }
}

/// <summary>
/// Exclusive Access (optional): opens on the agent's confirmed access language over the footage, gives an inside look,
/// the essential facts and an access call to action. Never written without a confirmed claim.
/// </summary>
public sealed class ExclusiveAccessPlanner : IVerticalFormatPlanner
{
    public VerticalCreativeFormat Format => VerticalCreativeFormat.ExclusiveAccess;

    public string RecipeVersion => "exclusive-access-v1";

    public VerticalCreativeRecipe? Plan(VerticalPlanningInput input)
    {
        var claim = input.AccessClaim;
        var footage = input.Footage.Where(video => video.IsReady && video.DurationMs > 0)
            .OrderBy(video => video.TourOrder).ThenBy(video => video.VideoId).ToArray();
        if (claim is null || footage.Sum(video => (long)video.DurationMs) < 10_000)
        {
            return null;
        }

        var builder = new RecipeBuilder(Format, RecipeVersion);
        var access = builder.Line("claim.access.text", claim.AccessText, VerticalCopySource.AgentClaim, $"ListingCreativeClaim:{claim.ClaimId:D}");
        var callToAction = builder.Template("cta.access", "Message me to request access");
        var essentials = builder.EssentialFacts(input.Facts);
        builder.Beat(VerticalBeatKind.AccessHook, 4_000, footage: footage[0], overlay: access, narration: access);
        var spans = RecipeBuilder.Split(18_000, footage.Length);
        for (var index = 0; index < footage.Length; index++)
        {
            builder.Beat(VerticalBeatKind.InsideLook, spans[index], footage: footage[index],
                sourceOffsetMs: index == 0 ? 4_000 : 0);
        }

        var usablePhotos = input.CurrentPhotos;
        var photo = usablePhotos.Count > 0 ? usablePhotos[0] : null;
        if (photo is not null)
        {
            builder.Beat(VerticalBeatKind.AccessDetails, 4_000, photo, overlay: essentials);
        }
        else
        {
            builder.Beat(VerticalBeatKind.AccessDetails, 4_000, footage: footage[^1], overlay: essentials);
        }

        var closing = RecipeBuilder.WithRole(usablePhotos, VerticalMediaRole.ClosingView).FirstOrDefault() ?? photo;
        if (closing is not null)
        {
            builder.Beat(VerticalBeatKind.CallToAction, 4_000, closing, overlay: callToAction);
        }
        else
        {
            builder.Beat(VerticalBeatKind.CallToAction, 4_000, footage: footage[^1], overlay: callToAction);
        }
        return builder.Build(30, false, access, VerticalNarrationStructure.AccessAnnouncement, VerticalCaptionDensity.Minimal, 5,
            callToAction, photo?.MediaId, footage[0].VideoId, false);
    }
}
