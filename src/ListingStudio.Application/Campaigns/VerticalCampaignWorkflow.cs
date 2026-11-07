using ListingStudio.Domain.Campaigns;

namespace ListingStudio.Application.Campaigns;

/// <summary>Plain-language names for the vertical formats, purposes and goals the agent sees.</summary>
public static class VerticalFormatNames
{
    public static string Name(VerticalCreativeFormat format) => format switch
    {
        VerticalCreativeFormat.WowFeature => "Wow feature",
        VerticalCreativeFormat.PriceFirst => "Price first",
        VerticalCreativeFormat.HostedPov => "Hosted walk-through",
        VerticalCreativeFormat.StoryReveal => "Story reveal",
        VerticalCreativeFormat.ExclusiveAccess => "Exclusive access",
        VerticalCreativeFormat.SpeedTour => "Speed tour",
        VerticalCreativeFormat.NeighborhoodTour => "Living in your town",
        VerticalCreativeFormat.LegacySingle => "Shared vertical video",
        _ => format.ToString(),
    };

    /// <summary>One sentence on what the video looks like.</summary>
    public static string Description(VerticalCreativeFormat format) => format switch
    {
        VerticalCreativeFormat.WowFeature => "Opens on the home's most striking space and loops back to it. Music only, no voice.",
        VerticalCreativeFormat.PriceFirst => "Leads with the verified price, city and key facts, then shows the best rooms.",
        VerticalCreativeFormat.HostedPov => "A walk-through in your own footage or approved narration, with word-by-word captions.",
        VerticalCreativeFormat.StoryReveal => "The problem you confirmed, the work, then the result.",
        VerticalCreativeFormat.ExclusiveAccess => "Your confirmed access or timing line over the property footage.",
        VerticalCreativeFormat.SpeedTour => "A 15-second hard-cut run through every room in walking order. Music only.",
        VerticalCreativeFormat.NeighborhoodTour => "Your approved nearby places and how far they are, then the home.",
        VerticalCreativeFormat.LegacySingle => "One edit shared by TikTok, Reels and Shorts.",
        _ => string.Empty,
    };

    public static string Purpose(VerticalCampaignPurpose purpose) => purpose switch
    {
        VerticalCampaignPurpose.Reach => "Reach new viewers",
        VerticalCampaignPurpose.Engagement => "Keep viewers watching",
        VerticalCampaignPurpose.Consideration => "Help buyers compare",
        VerticalCampaignPurpose.LeadGeneration => "Bring in inquiries",
        _ => purpose.ToString(),
    };

    public static string Strength(VerticalRecommendationStrength strength) => strength switch
    {
        VerticalRecommendationStrength.Strong => "Strong",
        VerticalRecommendationStrength.Good => "Good",
        VerticalRecommendationStrength.Weak => "Weak",
        VerticalRecommendationStrength.NotEligible => "Not ready",
        _ => strength.ToString(),
    };

    public static string Goal(VerticalCampaignGoal goal) => goal switch
    {
        VerticalCampaignGoal.WhatPriceBuys => "What $X buys in your town",
        VerticalCampaignGoal.GuessThePrice => "Guess the price",
        VerticalCampaignGoal.StandoutRoom => "Wait till you see the room",
        VerticalCampaignGoal.SpeedTour => "Speed tour",
        VerticalCampaignGoal.AgentHosted => "Agent-hosted tour",
        VerticalCampaignGoal.LuxuryShowcase => "Luxury showcase",
        VerticalCampaignGoal.LivingInTown => "Living in your town",
        VerticalCampaignGoal.MaximizeReach => "Maximize reach",
        VerticalCampaignGoal.BuyerInterest => "Generate buyer interest",
        VerticalCampaignGoal.OpenHouse => "Promote an open house",
        VerticalCampaignGoal.NewListing => "Announce a new listing",
        VerticalCampaignGoal.PriceChange => "Announce a price change",
        _ => goal.ToString(),
    };

    public static string GoalDescription(VerticalCampaignGoal goal) => goal switch
    {
        VerticalCampaignGoal.WhatPriceBuys => "Opens with \"What $400,000 buys in Wildwood, FL\", then the best rooms.",
        VerticalCampaignGoal.GuessThePrice => "Asks viewers to guess, and shows the price only at the end.",
        VerticalCampaignGoal.StandoutRoom => "Opens on the home's standout room with \"Wait till you see the kitchen\".",
        VerticalCampaignGoal.SpeedTour => "A fast cut through every room in 15 seconds.",
        VerticalCampaignGoal.AgentHosted => "You walk viewers through, in your footage, your narration or a consented presenter.",
        VerticalCampaignGoal.LuxuryShowcase => "Leads with the most striking space, for higher-end homes.",
        VerticalCampaignGoal.LivingInTown => "Shows your approved nearby places, then the home.",
        VerticalCampaignGoal.MaximizeReach => "Get the listing in front of as many local viewers as possible.",
        VerticalCampaignGoal.BuyerInterest => "Give serious buyers the details they compare.",
        VerticalCampaignGoal.OpenHouse => "Add the day and time you confirm to every video.",
        VerticalCampaignGoal.NewListing => "Open with a Just listed banner over the price and city.",
        VerticalCampaignGoal.PriceChange => "Lead with the reduction you confirm. Nothing about value is assumed.",
        _ => string.Empty,
    };

    /// <summary>
    /// The campaign message a goal starts from. The agent can still change it under Customize; the goal itself is
    /// stored separately and never picks a format.
    /// </summary>
    public static CampaignMarketingTemplate TemplateFor(VerticalCampaignGoal goal) => goal switch
    {
        VerticalCampaignGoal.MaximizeReach => CampaignMarketingTemplate.QuickTour,
        VerticalCampaignGoal.BuyerInterest => CampaignMarketingTemplate.FeatureSpotlight,
        VerticalCampaignGoal.OpenHouse => CampaignMarketingTemplate.OpenHouse,
        VerticalCampaignGoal.NewListing => CampaignMarketingTemplate.JustListed,
        VerticalCampaignGoal.PriceChange => CampaignMarketingTemplate.PriceDrop,
        VerticalCampaignGoal.WhatPriceBuys or VerticalCampaignGoal.GuessThePrice or VerticalCampaignGoal.SpeedTour
            => CampaignMarketingTemplate.QuickTour,
        VerticalCampaignGoal.StandoutRoom or VerticalCampaignGoal.LuxuryShowcase => CampaignMarketingTemplate.FeatureSpotlight,
        VerticalCampaignGoal.AgentHosted => CampaignMarketingTemplate.AgentShowcase,
        _ => CampaignMarketingTemplate.BalancedShowcase,
    };

    /// <summary>
    /// The campaign message for a campaign type plus an announcement: the announcement's message when there is one
    /// (Just listed, Open house or Price drop), otherwise the campaign type's own.
    /// </summary>
    public static CampaignMarketingTemplate TemplateFor(VerticalCampaignGoal goal, VerticalAnnouncement announcement) =>
        VerticalCampaignGoals.TemplateOf(announcement) ?? TemplateFor(goal);
}

/// <summary>
/// The agent's pick of vertical videos from one listing's recommendation: start from the recommended set, remove or
/// swap a video, or take every eligible format. Only eligible formats can ever be picked.
/// </summary>
public sealed class VerticalVideoPicker
{
    private readonly List<VerticalCreativeFormat> chosen = [];

    public VerticalVideoPicker(VerticalFormatRecommendationSet recommendation)
    {
        Recommendation = recommendation ?? throw new ArgumentNullException(nameof(recommendation));
        UseRecommended();
    }

    public VerticalFormatRecommendationSet Recommendation { get; }

    /// <summary>The picked formats, in rank order.</summary>
    public IReadOnlyList<VerticalCreativeFormat> Chosen => chosen;

    public IReadOnlyList<VerticalCreativeFormat> Recommended =>
        Recommendation.Recommended.Select(item => item.Format).ToArray();

    public IReadOnlyList<VerticalCreativeFormat> Eligible =>
        Recommendation.AllEligible.Select(item => item.Format).ToArray();

    public bool IsChosen(VerticalCreativeFormat format) => chosen.Contains(format);

    public bool CanChoose(VerticalCreativeFormat format) => Eligible.Contains(format);

    public void UseRecommended() => Replace(Recommended);

    public void UseAllEligible() => Replace(Eligible);

    /// <summary>Restores a saved pick (an earlier campaign's request); formats no longer eligible are dropped.</summary>
    public void Restore(VerticalVideoSelection selection, IReadOnlyList<VerticalCreativeFormat> formats)
    {
        ArgumentNullException.ThrowIfNull(formats);
        Replace(Recommendation.Select(selection, formats).Select(item => item.Format));
    }

    /// <summary>Adds or removes one eligible format; false when the format cannot be made.</summary>
    public bool Set(VerticalCreativeFormat format, bool include)
    {
        if (!include)
        {
            chosen.Remove(format);
            return true;
        }

        if (!CanChoose(format))
        {
            return false;
        }

        if (!chosen.Contains(format))
        {
            Replace(chosen.Append(format));
        }

        return true;
    }

    /// <summary>Swaps one picked video for another eligible format.</summary>
    public bool Substitute(VerticalCreativeFormat remove, VerticalCreativeFormat add)
    {
        if (!chosen.Contains(remove) || !CanChoose(add) || chosen.Contains(add))
        {
            return false;
        }

        Replace(chosen.Where(format => format != remove).Append(add));
        return true;
    }

    /// <summary>
    /// What to send: the recommended set and every eligible format keep their named selections, so the campaign
    /// records why each video was made; anything else is the agent's own list.
    /// </summary>
    public VerticalLibraryRequest ToRequest(VerticalCampaignGoal? goal)
    {
        if (chosen.Count == 0)
        {
            throw new InvalidOperationException("Pick at least one vertical video.");
        }

        if (chosen.SequenceEqual(Recommended))
        {
            return new VerticalLibraryRequest(VerticalVideoSelection.RecommendedThree, goal);
        }

        return chosen.SequenceEqual(Eligible)
            ? new VerticalLibraryRequest(VerticalVideoSelection.AllEligible, goal)
            : new VerticalLibraryRequest(VerticalVideoSelection.AgentChosen, goal, chosen.ToArray());
    }

    private void Replace(IEnumerable<VerticalCreativeFormat> formats)
    {
        var ordered = formats
            .Distinct()
            .Where(CanChoose)
            .OrderBy(format => Recommendation.For(format).Rank)
            .ToArray();
        chosen.Clear();
        chosen.AddRange(ordered);
    }
}
