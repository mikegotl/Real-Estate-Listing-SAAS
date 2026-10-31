using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Properties;

namespace ListingStudio.Application.Campaigns;

/// <summary>Ranks the vertical video formats one listing can support, with reasons an agent can read.</summary>
public interface IVerticalFormatRecommendationService
{
    /// <summary>Recommendations for a property in the signed-in user's workspace, or null if it is not there.</summary>
    Task<VerticalFormatRecommendationSet?> RecommendAsync(
        string userId,
        Guid propertyId,
        VerticalRecommendationContext context,
        CancellationToken cancellationToken = default);

    /// <summary>Recommendations for a property in one workspace (background work), or null if it is not there.</summary>
    Task<VerticalFormatRecommendationSet?> RecommendForOrganizationAsync(
        Guid organizationId,
        Guid propertyId,
        VerticalRecommendationContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The campaign request's own confirmations, which the recommendation may use but never assume: a consented presenter
/// and an agent-confirmed value message.
/// </summary>
public sealed record VerticalRecommendationContext(
    Guid? AgentPortraitId = null,
    bool PresenterConsentConfirmed = false,
    decimal? VerifiedPriceChangeAmount = null)
{
    public static VerticalRecommendationContext None { get; } = new();

    /// <summary>A verified price change is the only agent-confirmed value message today.</summary>
    public bool ValuePositioningConfirmed => VerifiedPriceChangeAmount > 0;

    public static VerticalRecommendationContext From(CampaignCreativeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new VerticalRecommendationContext(
            options.AgentPortraitId,
            options.AgentPortraitId.HasValue && options.AgentAvatarConsentConfirmed,
            options.PriceReductionVerified && options.PriceReductionAmount > 0 ? options.PriceReductionAmount : null);
    }
}

/// <summary>Everything the rules look at. Built from stored listing data; never from general market data.</summary>
public sealed record VerticalFormatInputs(
    PropertyType PropertyType,
    decimal ListingPrice,
    int Bedrooms,
    decimal Bathrooms,
    int? SquareFeet,
    decimal? LotSize,
    bool HasCityAndState,
    int ApprovedNeighborhoodPlaces,
    IReadOnlyList<VerticalPhotoInput> Photos,
    IReadOnlyList<VerticalFootageInput> Footage,
    bool HasApprovedNarration,
    VerticalPresenterInput Presenter,
    VerticalAgentConfirmations Confirmations,
    VerticalUnconfirmedMentions Mentions);

/// <summary>Whether a photo shows the property before or after work the agent confirmed.</summary>
public enum VerticalPhotoStoryRole
{
    None,
    Before,
    After,
}

/// <summary>One storyboard photo, by the scores the media analysis gave it.</summary>
public sealed record VerticalPhotoInput(
    Guid MediaId,
    PropertyMediaCategory? Category,
    bool AnalysisCompleted,
    int? QualityScore,
    int? HeroScore,
    int Width,
    int Height,
    VerticalPhotoStoryRole StoryRole = VerticalPhotoStoryRole.None);

/// <summary>
/// One uploaded property video. <paramref name="IsHostFootage"/> is set when it is tagged (by the agent or a
/// suggestion) as an agent intro, a hosted walk-through or a live replay; only such footage can carry Hosted POV.
/// </summary>
public sealed record VerticalFootageInput(Guid VideoId, int DurationMs, bool IsReady, bool IsHostFootage = false);

/// <summary>An agent portrait on file, and whether this request confirmed consent to use it as a presenter.</summary>
public sealed record VerticalPresenterInput(bool HasPortrait, bool ConsentConfirmed)
{
    public static VerticalPresenterInput None { get; } = new(false, false);

    public bool IsConsented => HasPortrait && ConsentConfirmed;
}

/// <summary>
/// Claims only an agent can confirm. Nothing in the product collects the story or access claims yet, so today they are
/// always false and Story Reveal and Exclusive Access stay ineligible.
/// </summary>
public sealed record VerticalAgentConfirmations(
    bool ValuePositioning = false,
    bool StoryProblem = false,
    bool StoryPayoff = false,
    bool AccessOrTiming = false)
{
    public static VerticalAgentConfirmations None { get; } = new();
}

/// <summary>
/// Claims the listing text or the AI story mentions without an agent confirming them. Used only to explain why a
/// format is not available; a mention is never evidence.
/// </summary>
public sealed record VerticalUnconfirmedMentions(bool Renovation = false, bool Exclusivity = false)
{
    public static VerticalUnconfirmedMentions None { get; } = new();

    private static readonly Regex RenovationPattern = new(
        @"\b(renovat\w*|remodel\w*|rehab\w*|restor(ed|ation)|upgraded|updated|brand[- ]new|fixer[- ]upper|transform\w*|before and after)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

    private static readonly Regex ExclusivityPattern = new(
        @"\b(exclusive\w*|off[- ]market|pre[- ]market|coming soon|private (showing|preview|tour)s?|first look|sneak peek|by appointment only|before it hits)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

    public static VerticalUnconfirmedMentions Find(IEnumerable<string?> texts)
    {
        var renovation = false;
        var exclusivity = false;
        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            try
            {
                renovation |= RenovationPattern.IsMatch(text);
                exclusivity |= ExclusivityPattern.IsMatch(text);
            }
            catch (RegexMatchTimeoutException)
            {
                // A mention only explains a missing input; skipping it never makes a format eligible.
            }
        }

        return new VerticalUnconfirmedMentions(renovation, exclusivity);
    }
}

public enum VerticalRecommendationStrength
{
    NotEligible,
    Weak,
    Good,
    Strong,
}

/// <summary>One format's result under one rule version.</summary>
public sealed record VerticalFormatRecommendation(
    VerticalCreativeFormat Format,
    int Score,
    VerticalRecommendationStrength Strength,
    VerticalCampaignPurpose Purpose,
    bool IsEligible,
    bool IsRecommended,
    int Rank,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> MissingInputs,
    string RuleVersion);

/// <summary>The ranked formats for one listing, and the snapshot stored with a library.</summary>
public sealed record VerticalFormatRecommendationSet(
    string RuleVersion,
    IReadOnlyList<VerticalFormatRecommendation> Formats,
    VerticalRecommendationInputSummary Inputs)
{
    private static readonly JsonSerializerOptions SnapshotOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false,
    };

    /// <summary>The default: up to three formats strong enough to make, in rank order.</summary>
    [JsonIgnore]
    public IReadOnlyList<VerticalFormatRecommendation> Recommended =>
        Formats.Where(format => format.IsRecommended).OrderBy(format => format.Rank).ToArray();

    /// <summary>Every format the listing can support, strong or not, in rank order.</summary>
    [JsonIgnore]
    public IReadOnlyList<VerticalFormatRecommendation> AllEligible =>
        Formats.Where(format => format.IsEligible).OrderBy(format => format.Rank).ToArray();

    public IReadOnlyList<VerticalFormatRecommendation> Select(VerticalVideoSelection selection) => selection switch
    {
        VerticalVideoSelection.RecommendedThree => Recommended,
        VerticalVideoSelection.AllEligible => AllEligible,
        _ => [],
    };

    public VerticalFormatRecommendation For(VerticalCreativeFormat format) =>
        Formats.Single(item => item.Format == format);

    /// <summary>
    /// The JSON stored in <c>CampaignVideoLibrary.RecommendationSnapshotJson</c>. It holds scores, reasons and input
    /// counts only, never listing text, so a later rule change cannot rewrite what a past campaign was shown.
    /// </summary>
    public string ToSnapshotJson() => JsonSerializer.Serialize(this, SnapshotOptions);

    public static VerticalFormatRecommendationSet? FromSnapshotJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<VerticalFormatRecommendationSet>(json, SnapshotOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Counts behind a recommendation, kept with the snapshot so a reviewer can see what the rules saw.</summary>
public sealed record VerticalRecommendationInputSummary(
    PropertyType PropertyType,
    bool HasPrice,
    bool HasCoreFacts,
    bool HasSize,
    bool HasCityAndState,
    int ApprovedNeighborhoodPlaces,
    int StoryboardPhotos,
    int AnalyzedPhotos,
    int UsablePhotos,
    int StandoutFeaturePhotos,
    int ReadyFootageSeconds,
    int HostFootageSeconds,
    bool HasApprovedNarration,
    bool HasPresenterPortrait,
    bool PresenterConsentConfirmed,
    bool ValuePositioningConfirmed,
    bool StoryClaimConfirmed,
    bool AccessClaimConfirmed,
    bool HasBeforeAndAfterPhotos);

/// <summary>
/// The deterministic first-release rules. Change the scoring only by adding a new version: a library keeps the
/// snapshot it was created with. No AI here; AI may later rank media inside a format, never decide eligibility.
/// </summary>
public static class VerticalFormatRules
{
    public const string Version = "vertical-rules-v1";

    /// <summary>A format needs at least this score to be recommended by default; weaker ones only via All eligible.</summary>
    public const int RecommendThreshold = 55;

    public const int StrongThreshold = 75;

    public const int MaximumRecommended = 3;

    /// <summary>Formats the rules rank, in tie-break order (earlier wins a tie).</summary>
    public static IReadOnlyList<VerticalCreativeFormat> Formats { get; } =
    [
        VerticalCreativeFormat.WowFeature,
        VerticalCreativeFormat.PriceFirst,
        VerticalCreativeFormat.HostedPov,
        VerticalCreativeFormat.StoryReveal,
        VerticalCreativeFormat.ExclusiveAccess,
    ];

    public static VerticalCampaignPurpose PurposeOf(VerticalCreativeFormat format) => format switch
    {
        VerticalCreativeFormat.WowFeature => VerticalCampaignPurpose.Reach,
        VerticalCreativeFormat.PriceFirst => VerticalCampaignPurpose.Consideration,
        VerticalCreativeFormat.HostedPov => VerticalCampaignPurpose.LeadGeneration,
        VerticalCreativeFormat.StoryReveal => VerticalCampaignPurpose.Engagement,
        VerticalCreativeFormat.ExclusiveAccess => VerticalCampaignPurpose.LeadGeneration,
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    private const int MinimumUsablePhotosForWow = 4;
    private const int MinimumUsablePhotos = 3;
    private const int MinimumHostedFootageSeconds = 15;
    private const int MinimumAccessFootageSeconds = 10;

    private static readonly HashSet<PropertyMediaCategory> FeatureCategories =
    [
        PropertyMediaCategory.FrontExterior,
        PropertyMediaCategory.RearExterior,
        PropertyMediaCategory.Aerial,
        PropertyMediaCategory.Entry,
        PropertyMediaCategory.LivingRoom,
        PropertyMediaCategory.Kitchen,
        PropertyMediaCategory.DiningRoom,
        PropertyMediaCategory.PrimaryBedroom,
        PropertyMediaCategory.PrimaryBathroom,
        PropertyMediaCategory.Office,
        PropertyMediaCategory.Pool,
        PropertyMediaCategory.Patio,
        PropertyMediaCategory.Backyard,
        PropertyMediaCategory.Community,
    ];

    public static VerticalFormatRecommendationSet Evaluate(VerticalFormatInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var facts = new Facts(inputs);
        var scored = new[]
        {
            ScoreWowFeature(facts),
            ScorePriceFirst(facts),
            ScoreHostedPov(facts),
            ScoreStoryReveal(facts),
            ScoreExclusiveAccess(facts),
        };

        var ranked = scored
            .OrderByDescending(item => item.IsEligible)
            .ThenByDescending(item => item.Score)
            .ThenBy(item => TieBreak(item.Format))
            .ToArray();
        var recommended = PickRecommended(ranked);

        var results = ranked.Select((item, index) =>
        {
            var reasons = item.Reasons.ToList();
            if (recommended.TryGetValue(item.Format, out var note) && note is not null)
            {
                reasons.Add(note);
            }
            else if (item.IsEligible && !recommended.ContainsKey(item.Format))
            {
                reasons.Add(item.Score < RecommendThreshold
                    ? "Not in the default set because the listing only supports a weak version of it; choose Generate all eligible formats to make it anyway."
                    : "Not in the default set because three stronger formats were chosen; choose Generate all eligible formats to make it too.");
            }

            return new VerticalFormatRecommendation(
                item.Format,
                item.Score,
                StrengthOf(item.IsEligible, item.Score),
                PurposeOf(item.Format),
                item.IsEligible,
                recommended.ContainsKey(item.Format),
                index + 1,
                reasons,
                item.Missing,
                Version);
        }).ToArray();

        return new VerticalFormatRecommendationSet(Version, results, facts.Summary());
    }

    public static VerticalRecommendationStrength StrengthOf(bool eligible, int score) =>
        !eligible ? VerticalRecommendationStrength.NotEligible
        : score >= StrongThreshold ? VerticalRecommendationStrength.Strong
        : score >= RecommendThreshold ? VerticalRecommendationStrength.Good
        : VerticalRecommendationStrength.Weak;

    private static int TieBreak(VerticalCreativeFormat format)
    {
        for (var index = 0; index < Formats.Count; index++)
        {
            if (Formats[index] == format)
            {
                return index;
            }
        }

        return int.MaxValue;
    }

    /// <summary>
    /// The top three formats at or above the threshold, swapped so the set has a reach video and a consideration or
    /// lead video when an eligible, strong-enough format exists for each. Never fills a slot with a weak format.
    /// </summary>
    private static Dictionary<VerticalCreativeFormat, string?> PickRecommended(IReadOnlyList<Scored> ranked)
    {
        var candidates = ranked.Where(item => item.IsEligible && item.Score >= RecommendThreshold).ToList();
        var picked = candidates.Take(MaximumRecommended).ToList();
        var notes = new Dictionary<VerticalCreativeFormat, string?>();
        foreach (var group in CoverageGroups)
        {
            if (picked.Any(item => group.Purposes.Contains(PurposeOf(item.Format))))
            {
                continue;
            }

            var addition = candidates.FirstOrDefault(item =>
                !picked.Contains(item) && group.Purposes.Contains(PurposeOf(item.Format)));
            if (addition is null)
            {
                continue;
            }

            var removable = picked
                .AsEnumerable()
                .Reverse()
                .FirstOrDefault(item => CoverageGroups.All(other => other == group
                    || !other.Purposes.Contains(PurposeOf(item.Format))
                    || picked.Any(peer => peer != item && other.Purposes.Contains(PurposeOf(peer.Format)))));
            if (removable is null)
            {
                continue;
            }

            picked.Remove(removable);
            picked.Add(addition);
            notes[addition.Format] = $"Included in place of a higher-scoring format so the set has a {group.Label} video.";
        }

        foreach (var item in picked)
        {
            notes.TryAdd(item.Format, null);
        }

        return notes;
    }

    private static readonly (string Label, VerticalCampaignPurpose[] Purposes)[] CoverageGroups =
    [
        ("reach", [VerticalCampaignPurpose.Reach]),
        ("consideration or lead", [VerticalCampaignPurpose.Consideration, VerticalCampaignPurpose.LeadGeneration]),
    ];

    private static Scored ScoreWowFeature(Facts facts)
    {
        var reasons = new List<string>();
        var missing = new List<string>();
        if (facts.AnalyzedPhotos == 0 && facts.StoryboardPhotos > 0)
        {
            missing.Add("Photo analysis has not finished, so no standout feature is confirmed yet.");
        }

        var distinctiveness = facts.BestHeroScore * 35 / 100;
        var prominence = Math.Min(facts.FeatureRooms.Length, 3) * 20 / 3;
        var usable = Math.Min(facts.UsablePhotos, 8) * 15 / 8;
        var quality = facts.TopQualityAverage * 30 / 100;
        var standout = facts.StandoutFeature;
        if (standout is not null)
        {
            reasons.Add($"A standout {RoomLabel(standout.Category)} photo (hero score {standout.HeroScore}) can open the video.");
        }
        else if (facts.AnalyzedPhotos > 0)
        {
            missing.Add("A standout feature photo (hero score 70 or more and quality 60 or more) of a main room or outdoor space.");
        }

        if (facts.FeatureRooms.Length > 0)
        {
            reasons.Add($"Strong photos of {facts.FeatureRooms.Length} feature area{Plural(facts.FeatureRooms.Length)}: {string.Join(", ", facts.FeatureRooms.Select(room => RoomLabel(room)))}.");
        }

        if (facts.UsablePhotos < MinimumUsablePhotosForWow)
        {
            missing.Add($"At least {MinimumUsablePhotosForWow} usable storyboard photos ({facts.UsablePhotos} now).");
        }
        else
        {
            reasons.Add($"{facts.UsablePhotos} usable storyboard photos.");
        }

        var eligible = standout is not null && facts.UsablePhotos >= MinimumUsablePhotosForWow;
        return new Scored(
            VerticalCreativeFormat.WowFeature,
            Clamp(distinctiveness + prominence + usable + quality),
            eligible,
            reasons,
            missing);
    }

    private static Scored ScorePriceFirst(Facts facts)
    {
        var reasons = new List<string>();
        var missing = new List<string>();
        var score = 0;
        if (facts.HasPrice)
        {
            score += 30;
            reasons.Add("The listing price is entered.");
        }
        else
        {
            missing.Add("The listing price.");
        }

        if (facts.HasCoreFacts)
        {
            score += 20;
            reasons.Add(facts.IsLand ? "The lot size is entered." : "Bedrooms and bathrooms are entered.");
        }
        else
        {
            missing.Add(facts.IsLand ? "The lot size." : "Bedrooms and bathrooms.");
        }

        if (facts.HasSize)
        {
            score += 10;
        }
        else if (!facts.IsLand)
        {
            missing.Add("Square footage (optional, makes the price card stronger).");
        }

        if (facts.Inputs.HasCityAndState)
        {
            score += 15;
            reasons.Add("City and state are entered for location context.");
        }
        else
        {
            missing.Add("City and state.");
        }

        var places = Math.Min(facts.Inputs.ApprovedNeighborhoodPlaces, 3);
        if (places > 0)
        {
            score += places * 10 / 3;
            reasons.Add($"{facts.Inputs.ApprovedNeighborhoodPlaces} approved nearby place{Plural(facts.Inputs.ApprovedNeighborhoodPlaces)} for location context.");
        }

        if (facts.Inputs.Confirmations.ValuePositioning)
        {
            score += 15;
            reasons.Add("The agent confirmed a value message (verified price change).");
        }
        else
        {
            missing.Add("An agent-confirmed value message, such as a verified price change (optional). Affordability is never inferred from market data.");
        }

        if (facts.UsablePhotos < MinimumUsablePhotos)
        {
            missing.Add($"At least {MinimumUsablePhotos} usable storyboard photos ({facts.UsablePhotos} now).");
        }

        var eligible = facts.HasPrice && facts.HasCoreFacts && facts.Inputs.HasCityAndState
            && facts.UsablePhotos >= MinimumUsablePhotos;
        return new Scored(VerticalCreativeFormat.PriceFirst, Clamp(score), eligible, reasons, missing);
    }

    private static Scored ScoreHostedPov(Facts facts)
    {
        var reasons = new List<string>();
        var missing = new List<string>();
        var score = 0;
        var footageSeconds = facts.HostFootageSeconds;
        var hasFootage = footageSeconds >= MinimumHostedFootageSeconds;
        if (hasFootage)
        {
            score += Math.Min(footageSeconds, 45) * 35 / 45;
            reasons.Add($"{footageSeconds} seconds of hosted or intro footage is ready.");
        }
        else if (facts.ReadyFootageSeconds >= MinimumHostedFootageSeconds)
        {
            missing.Add("Host footage: the property video is ready, but none is tagged as an agent intro, hosted walk-through or live replay.");
        }
        else
        {
            missing.Add($"At least {MinimumHostedFootageSeconds} seconds of processed host footage (agent intro, hosted walk-through or live replay).");
        }

        if (facts.Inputs.HasApprovedNarration)
        {
            score += 25;
            reasons.Add("The agent's own narration is approved for marketing.");
        }
        else
        {
            missing.Add("Approved narration (room-by-room narration or an accepted voiceover script).");
        }

        var presenter = facts.Inputs.Presenter;
        if (presenter.IsConsented)
        {
            score += 25;
            reasons.Add("A presenter portrait is on file and consent to use it was confirmed.");
        }
        else if (presenter.HasPortrait)
        {
            missing.Add("Presenter consent: a portrait is on file but consent to use it as a presenter was not confirmed.");
        }
        else
        {
            missing.Add("A consented presenter portrait (optional when footage or narration is ready).");
        }

        score += Math.Min(facts.UsablePhotos, 8) * 15 / 8;
        var hasHost = hasFootage || facts.Inputs.HasApprovedNarration || presenter.IsConsented;
        var hasVisuals = hasFootage || facts.UsablePhotos >= MinimumUsablePhotos;
        if (!hasVisuals)
        {
            missing.Add($"At least {MinimumUsablePhotos} usable storyboard photos or ready property video.");
        }

        return new Scored(VerticalCreativeFormat.HostedPov, Clamp(score), hasHost && hasVisuals, reasons, missing);
    }

    private static Scored ScoreStoryReveal(Facts facts)
    {
        var reasons = new List<string>();
        var missing = new List<string>();
        var confirmations = facts.Inputs.Confirmations;
        var score = 0;
        if (confirmations.StoryProblem)
        {
            score += 25;
            reasons.Add("The agent confirmed the problem the home solved.");
        }

        if (confirmations.StoryPayoff)
        {
            score += 25;
            reasons.Add("The agent confirmed the result and payoff.");
        }

        if (!confirmations.StoryProblem || !confirmations.StoryPayoff)
        {
            missing.Add("An agent-confirmed story: the problem and the payoff.");
            if (facts.Inputs.Mentions.Renovation)
            {
                missing.Add("The listing text or AI story mentions a renovation or update, but no agent has confirmed it, so it cannot be used as a story.");
            }
        }

        if (facts.HasBeforeAndAfter)
        {
            score += 30;
            reasons.Add("Before and after photos are tagged.");
        }
        else
        {
            if (facts.HasAfterOnly)
            {
                score += 10;
            }

            missing.Add("Before and after photos (preferred).");
        }

        score += facts.TopQualityAverage * 20 / 100;
        var eligible = confirmations.StoryProblem && confirmations.StoryPayoff && facts.UsablePhotos >= MinimumUsablePhotos;
        if (facts.UsablePhotos < MinimumUsablePhotos)
        {
            missing.Add($"At least {MinimumUsablePhotos} usable storyboard photos ({facts.UsablePhotos} now).");
        }

        return new Scored(VerticalCreativeFormat.StoryReveal, Clamp(score), eligible, reasons, missing);
    }

    private static Scored ScoreExclusiveAccess(Facts facts)
    {
        var reasons = new List<string>();
        var missing = new List<string>();
        var score = 0;
        var confirmed = facts.Inputs.Confirmations.AccessOrTiming;
        if (confirmed)
        {
            score += 40;
            reasons.Add("The agent confirmed the access or timing language.");
        }
        else
        {
            missing.Add("Agent-confirmed access or timing language (for example a dated private preview).");
            if (facts.Inputs.Mentions.Exclusivity)
            {
                missing.Add("The listing text or AI story mentions exclusive or early access, but no agent has confirmed it, so it cannot be used.");
            }
        }

        var footageSeconds = facts.ReadyFootageSeconds;
        var hasFootage = footageSeconds >= MinimumAccessFootageSeconds;
        if (hasFootage)
        {
            score += Math.Min(footageSeconds, 30) * 40 / 30;
            reasons.Add($"{footageSeconds} seconds of property video is ready for an inside look.");
        }
        else
        {
            missing.Add($"At least {MinimumAccessFootageSeconds} seconds of processed property video.");
        }

        score += Math.Min(facts.UsablePhotos, 8) * 20 / 8;
        return new Scored(VerticalCreativeFormat.ExclusiveAccess, Clamp(score), confirmed && hasFootage, reasons, missing);
    }

    private static int Clamp(int score) => Math.Clamp(score, 0, 100);

    private static string Plural(int count) => count == 1 ? string.Empty : "s";

    private static string RoomLabel(PropertyMediaCategory? category) => category switch
    {
        PropertyMediaCategory.FrontExterior => "front exterior",
        PropertyMediaCategory.RearExterior => "rear exterior",
        PropertyMediaCategory.Aerial => "aerial",
        PropertyMediaCategory.Entry => "entry",
        PropertyMediaCategory.LivingRoom => "living room",
        PropertyMediaCategory.Kitchen => "kitchen",
        PropertyMediaCategory.DiningRoom => "dining room",
        PropertyMediaCategory.PrimaryBedroom => "primary bedroom",
        PropertyMediaCategory.PrimaryBathroom => "primary bathroom",
        PropertyMediaCategory.Office => "office",
        PropertyMediaCategory.Pool => "pool",
        PropertyMediaCategory.Patio => "patio",
        PropertyMediaCategory.Backyard => "backyard",
        PropertyMediaCategory.Community => "community",
        _ => "feature",
    };

    private sealed record Scored(
        VerticalCreativeFormat Format,
        int Score,
        bool IsEligible,
        IReadOnlyList<string> Reasons,
        IReadOnlyList<string> Missing);

    /// <summary>Derived counts, computed once so every format reads the same facts.</summary>
    private sealed class Facts
    {
        public Facts(VerticalFormatInputs inputs)
        {
            Inputs = inputs;
            IsLand = inputs.PropertyType == PropertyType.Land;
            HasPrice = inputs.ListingPrice > 0;
            HasCoreFacts = IsLand ? inputs.LotSize > 0 : inputs.Bedrooms > 0 && inputs.Bathrooms > 0;
            HasSize = IsLand ? inputs.LotSize > 0 : inputs.SquareFeet > 0;
            var photos = inputs.Photos ?? [];
            StoryboardPhotos = photos.Count;
            var analyzed = photos.Where(photo => photo.AnalysisCompleted).ToArray();
            AnalyzedPhotos = analyzed.Length;
            var usable = photos.Where(IsUsable).ToArray();
            UsablePhotos = usable.Length;
            var usableAnalyzed = usable.Where(photo => photo.AnalysisCompleted).ToArray();
            BestHeroScore = usableAnalyzed.Select(photo => Math.Clamp(photo.HeroScore ?? 0, 0, 100)).DefaultIfEmpty(0).Max();
            TopQualityAverage = usableAnalyzed.Length == 0
                ? 0
                : (int)usableAnalyzed
                    .Select(photo => Math.Clamp(photo.QualityScore ?? 0, 0, 100))
                    .OrderDescending()
                    .Take(5)
                    .Average();
            var standouts = usableAnalyzed
                .Where(photo => photo.HeroScore >= 70 && photo.QualityScore >= 60
                    && photo.Category is { } category && FeatureCategories.Contains(category))
                .OrderByDescending(photo => photo.HeroScore)
                .ThenByDescending(photo => photo.QualityScore)
                .ThenBy(photo => photo.MediaId)
                .ToArray();
            StandoutFeature = standouts.FirstOrDefault();
            StandoutFeaturePhotos = standouts.Length;
            FeatureRooms = usableAnalyzed
                .Where(photo => photo.HeroScore >= 60 && photo.QualityScore >= 55
                    && photo.Category is { } category && FeatureCategories.Contains(category))
                .Select(photo => photo.Category!.Value)
                .Distinct()
                .Order()
                .ToArray();
            ReadyFootageSeconds = (int)Math.Min(
                (inputs.Footage ?? []).Where(video => video.IsReady && video.DurationMs > 0).Sum(video => (long)video.DurationMs) / 1000,
                int.MaxValue);
            HostFootageSeconds = (int)Math.Min(
                (inputs.Footage ?? []).Where(video => video.IsReady && video.IsHostFootage && video.DurationMs > 0)
                    .Sum(video => (long)video.DurationMs) / 1000,
                int.MaxValue);
            HasBeforeAndAfter = usable.Any(photo => photo.StoryRole == VerticalPhotoStoryRole.Before)
                && usable.Any(photo => photo.StoryRole == VerticalPhotoStoryRole.After);
            HasAfterOnly = !HasBeforeAndAfter && usable.Any(photo => photo.StoryRole == VerticalPhotoStoryRole.After);
        }

        public VerticalFormatInputs Inputs { get; }

        public bool IsLand { get; }

        public bool HasPrice { get; }

        public bool HasCoreFacts { get; }

        public bool HasSize { get; }

        public int StoryboardPhotos { get; }

        public int AnalyzedPhotos { get; }

        public int UsablePhotos { get; }

        public int BestHeroScore { get; }

        public int TopQualityAverage { get; }

        public VerticalPhotoInput? StandoutFeature { get; }

        public int StandoutFeaturePhotos { get; }

        public PropertyMediaCategory[] FeatureRooms { get; }

        public int ReadyFootageSeconds { get; }

        public int HostFootageSeconds { get; }

        public bool HasBeforeAndAfter { get; }

        public bool HasAfterOnly { get; }

        /// <summary>
        /// Analyzed photos count by their quality score; photos still waiting for analysis count when they are at
        /// least 720 pixels on the short side, so a slow analysis does not hide a listing's photos.
        /// </summary>
        private static bool IsUsable(VerticalPhotoInput photo) => photo.AnalysisCompleted
            ? photo.QualityScore >= 50
            : Math.Min(photo.Width, photo.Height) >= 720;

        public VerticalRecommendationInputSummary Summary() => new(
            Inputs.PropertyType,
            HasPrice,
            HasCoreFacts,
            HasSize,
            Inputs.HasCityAndState,
            Inputs.ApprovedNeighborhoodPlaces,
            StoryboardPhotos,
            AnalyzedPhotos,
            UsablePhotos,
            StandoutFeaturePhotos,
            ReadyFootageSeconds,
            HostFootageSeconds,
            Inputs.HasApprovedNarration,
            Inputs.Presenter.HasPortrait,
            Inputs.Presenter.ConsentConfirmed,
            Inputs.Confirmations.ValuePositioning,
            Inputs.Confirmations.StoryProblem && Inputs.Confirmations.StoryPayoff,
            Inputs.Confirmations.AccessOrTiming,
            HasBeforeAndAfter);
    }
}
