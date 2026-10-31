using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Properties;

namespace ListingStudio.Application.Campaigns;

public enum VerticalReadinessState
{
    /// <summary>In place and confirmed (entered by the agent, or measured from the media).</summary>
    Ready,

    /// <summary>Not there yet.</summary>
    Missing,

    /// <summary>There, but not confirmed by the agent: a suggestion, a pending analysis or an unconfirmed mention.</summary>
    Unverified,
}

/// <summary>One input a format needs, in words an agent understands.</summary>
public sealed record VerticalReadinessItem(string Input, VerticalReadinessState State, bool IsRequired, string Explanation);

/// <summary>Whether one format can be made, and why.</summary>
public sealed record VerticalFormatReadiness(
    VerticalCreativeFormat Format,
    bool IsReady,
    IReadOnlyList<VerticalReadinessItem> Items);

/// <summary>
/// Explains, input by input, what each format has and lacks. Whether a format is ready always matches the
/// recommendation engine's eligibility for the same listing, so the two never disagree.
/// </summary>
public static class VerticalReadinessEvaluator
{
    public static IReadOnlyList<VerticalFormatReadiness> Evaluate(
        VerticalPlanningInput input,
        VerticalFormatRecommendationSet recommendation)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(recommendation);
        return VerticalFormatRules.Formats
            .Select(format => new VerticalFormatReadiness(
                format,
                recommendation.For(format).IsEligible,
                format switch
                {
                    VerticalCreativeFormat.WowFeature => WowFeature(input),
                    VerticalCreativeFormat.PriceFirst => PriceFirst(input),
                    VerticalCreativeFormat.HostedPov => HostedPov(input),
                    VerticalCreativeFormat.StoryReveal => StoryReveal(input),
                    _ => ExclusiveAccess(input),
                }))
            .ToArray();
    }

    private static List<VerticalReadinessItem> WowFeature(VerticalPlanningInput input)
    {
        var items = new List<VerticalReadinessItem>();
        var usable = input.UsablePhotos;
        var strong = usable
            .Where(photo => photo.AnalysisCompleted && photo.HeroScore >= 70 && photo.QualityScore >= 60
                && VerticalMediaRoles.IsFeatureCategory(photo.Category))
            .ToArray();
        var pending = input.Photos.Count(photo => !photo.AnalysisCompleted);
        if (strong.Length > 0)
        {
            items.Add(new("Visually strong feature", VerticalReadinessState.Ready, true,
                $"{strong.Length} photo{Plural(strong.Length)} score{(strong.Length == 1 ? "s" : string.Empty)} 70 or more as a standout feature."));
        }
        else if (pending > 0)
        {
            items.Add(new("Visually strong feature", VerticalReadinessState.Unverified, true,
                $"{pending} photo{Plural(pending)} still waiting for analysis, so no standout feature is confirmed yet."));
        }
        else
        {
            items.Add(new("Visually strong feature", VerticalReadinessState.Missing, true,
                "No photo of a main room or outdoor space scores 70 or more. Add a striking photo of the best feature."));
        }

        var chosen = input.Photos.FirstOrDefault(photo => photo.HasFromAgent(VerticalMediaRole.HeroFeature));
        var suggested = input.Photos.FirstOrDefault(photo => photo.Has(VerticalMediaRole.HeroFeature));
        items.Add(chosen is not null
            ? new("Opening shot", VerticalReadinessState.Ready, false, $"You chose {Label(chosen)} as the hero feature.")
            : suggested is not null
                ? new("Opening shot", VerticalReadinessState.Unverified, false,
                    $"Suggested from photo analysis: {Label(suggested)}. Confirm it or pick another hero feature.")
                : new("Opening shot", VerticalReadinessState.Missing, false, "No hero feature is chosen or suggested yet."));
        items.Add(UsablePhotos(usable.Count, 4));
        return items;
    }

    private static List<VerticalReadinessItem> PriceFirst(VerticalPlanningInput input)
    {
        var facts = input.Facts;
        var land = facts.PropertyType == PropertyType.Land;
        var items = new List<VerticalReadinessItem>
        {
            facts.PriceText is { } price
                ? new("Listing price", VerticalReadinessState.Ready, true, $"{price}, entered by the agent.")
                : new("Listing price", VerticalReadinessState.Missing, true, "Enter the listing price."),
            facts.EssentialFactsText is { } essentials
                ? new(land ? "Lot size" : "Bedrooms and bathrooms", VerticalReadinessState.Ready, true, essentials)
                : new(land ? "Lot size" : "Bedrooms and bathrooms", VerticalReadinessState.Missing, true,
                    land ? "Enter the lot size." : "Enter the bedrooms and bathrooms."),
            facts.CityStateText is { } city
                ? new("City and state", VerticalReadinessState.Ready, true, city)
                : new("City and state", VerticalReadinessState.Missing, true, "Enter the city and state."),
            input.VerifiedPriceChange > 0
                ? new("Value message", VerticalReadinessState.Ready, false, "A verified price change is confirmed for this campaign.")
                : new("Value message", VerticalReadinessState.Missing, false,
                    "Optional: confirm a price change to lead with it. Affordability is never inferred from market data."),
            facts.ApprovedNeighborhoodPlaces > 0
                ? new("Nearby places", VerticalReadinessState.Ready, false,
                    $"{facts.ApprovedNeighborhoodPlaces} approved nearby place{Plural(facts.ApprovedNeighborhoodPlaces)}.")
                : new("Nearby places", VerticalReadinessState.Missing, false, "Optional: approve nearby places for location context."),
            UsablePhotos(input.UsablePhotos.Count, 3),
        };
        return items;
    }

    private static List<VerticalReadinessItem> HostedPov(VerticalPlanningInput input)
    {
        var items = new List<VerticalReadinessItem>();
        var host = input.Footage.Where(video => video.IsHostFootage).ToArray();
        var confirmed = host.Where(video => video.Roles.Any(role => role.Source == VerticalMediaRoleSource.Agent
            && role.Role is VerticalMediaRole.AgentIntro or VerticalMediaRole.HostedWalkthrough or VerticalMediaRole.LiveReplay)).ToArray();
        var seconds = host.Sum(video => (long)video.DurationMs) / 1000;
        if (confirmed.Length > 0)
        {
            items.Add(new("Host footage", VerticalReadinessState.Ready, false,
                $"{seconds} seconds of footage you tagged as an intro, hosted walk-through or live replay."));
        }
        else if (host.Length > 0)
        {
            items.Add(new("Host footage", VerticalReadinessState.Unverified, false,
                $"{seconds} seconds of spoken property video looks like a hosted walk-through. Confirm the tag."));
        }
        else
        {
            var processing = input.Footage.Count(video => !video.IsReady);
            items.Add(new("Host footage", VerticalReadinessState.Missing, false, processing > 0
                ? $"{processing} video{Plural(processing)} still processing."
                : "No property video tagged as an agent intro, hosted walk-through or live replay."));
        }

        items.Add(input.Narration.Count > 0
            ? new("Approved voiceover", VerticalReadinessState.Ready, false,
                $"{input.Narration.Count} line{Plural(input.Narration.Count)} of your own narration are approved.")
            : new("Approved voiceover", VerticalReadinessState.Missing, false,
                "No room-by-room narration or accepted voiceover script."));
        items.Add(input.Presenter.IsConsented
            ? new("Consented presenter", VerticalReadinessState.Ready, false, "Your portrait is on file and consent is confirmed.")
            : input.Presenter.HasPortrait
                ? new("Consented presenter", VerticalReadinessState.Unverified, false,
                    "Your portrait is on file, but consent to use it as a presenter is not confirmed.")
                : new("Consented presenter", VerticalReadinessState.Missing, false, "No presenter portrait is on file."));
        items.Add(new("One host input", input.Footage.Any(video => video.IsHostFootage && video.DurationMs >= 15_000)
                || input.Narration.Count > 0 || input.Presenter.IsConsented
                ? VerticalReadinessState.Ready
                : VerticalReadinessState.Missing, true,
            "Hosted POV needs at least one of: 15 seconds of host footage, approved voiceover, or a consented presenter."));
        return items;
    }

    private static List<VerticalReadinessItem> StoryReveal(VerticalPlanningInput input)
    {
        var items = new List<VerticalReadinessItem>();
        if (input.StoryClaim is not null)
        {
            items.Add(new("Approved story claim", VerticalReadinessState.Ready, true, "You confirmed the problem and the result."));
        }
        else if (input.Mentions.Renovation)
        {
            items.Add(new("Approved story claim", VerticalReadinessState.Unverified, true,
                "The listing text or AI story mentions a renovation or update, but you have not confirmed it. Confirm the problem and result to use it."));
        }
        else
        {
            items.Add(new("Approved story claim", VerticalReadinessState.Missing, true,
                "Confirm the problem the home had and the result."));
        }

        var before = input.UsablePhotos.Count(photo => photo.HasFromAgent(VerticalMediaRole.BeforeRenovation));
        var after = input.UsablePhotos.Count(photo => photo.HasFromAgent(VerticalMediaRole.AfterRenovation));
        items.Add(before > 0 && after > 0
            ? new("Before and after photos", VerticalReadinessState.Ready, false, $"{before} before and {after} after photo{Plural(after)} tagged.")
            : new("Before and after photos", VerticalReadinessState.Missing, false,
                "Preferred: tag at least one before and one after photo. Without them the problem is shown as a text card."));
        items.Add(UsablePhotos(input.UsablePhotos.Count, 3));
        return items;
    }

    private static List<VerticalReadinessItem> ExclusiveAccess(VerticalPlanningInput input)
    {
        var items = new List<VerticalReadinessItem>();
        if (input.AccessClaim is not null)
        {
            items.Add(new("Confirmed access language", VerticalReadinessState.Ready, true, "You confirmed the access or timing language."));
        }
        else if (input.Mentions.Exclusivity)
        {
            items.Add(new("Confirmed access language", VerticalReadinessState.Unverified, true,
                "The listing text or AI story mentions exclusive or early access, but you have not confirmed it."));
        }
        else
        {
            items.Add(new("Confirmed access language", VerticalReadinessState.Missing, true,
                "Optional format: confirm private, early or dated access to offer it."));
        }

        var seconds = input.Footage.Where(video => video.IsReady).Sum(video => (long)video.DurationMs) / 1000;
        items.Add(seconds >= 10
            ? new("Property footage", VerticalReadinessState.Ready, true, $"{seconds} seconds of property video is ready.")
            : new("Property footage", VerticalReadinessState.Missing, true, "At least 10 seconds of processed property video."));
        return items;
    }

    private static VerticalReadinessItem UsablePhotos(int count, int minimum) => count >= minimum
        ? new("Usable photos", VerticalReadinessState.Ready, true, $"{count} usable storyboard photos.")
        : new("Usable photos", VerticalReadinessState.Missing, true, $"At least {minimum} usable storyboard photos ({count} now).");

    internal static string Label(VerticalPlanningPhoto photo) =>
        !string.IsNullOrWhiteSpace(photo.Stop) ? $"the {photo.Stop.Trim()} photo" : "a storyboard photo";

    private static string Plural(long count) => count == 1 ? string.Empty : "s";
}
