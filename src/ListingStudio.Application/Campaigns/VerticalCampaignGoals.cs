using ListingStudio.Domain.Campaigns;

namespace ListingStudio.Application.Campaigns;

/// <summary>
/// An optional announcement added on top of any campaign type. It only sets the campaign message (the marketing
/// template); the open house time and price reduction are still entered and confirmed by the agent.
/// </summary>
public enum VerticalAnnouncement
{
    None,
    JustListed,
    OpenHouse,
    PriceDrop,
}

/// <summary>What the research behind a campaign type found, shown beside the choice.</summary>
public sealed record VerticalCampaignGoalEvidence(string Summary, string ExampleLabel, string ExampleUrl);

/// <summary>
/// The campaign types agents pick from on the Campaign tab, taken from the November 2026 TikTok home tour research
/// (the "Which TikTok home tours get the most views and likes" article). Each type leads with one video format; the
/// listing's media and confirmed facts still decide which formats can be made.
/// </summary>
public static class VerticalCampaignGoals
{
    /// <summary>The campaign types offered today, in display order. The original five goals stay readable only.</summary>
    public static IReadOnlyList<VerticalCampaignGoal> Offered { get; } =
    [
        VerticalCampaignGoal.WhatPriceBuys,
        VerticalCampaignGoal.GuessThePrice,
        VerticalCampaignGoal.StandoutRoom,
        VerticalCampaignGoal.SpeedTour,
        VerticalCampaignGoal.AgentHosted,
        VerticalCampaignGoal.LuxuryShowcase,
        VerticalCampaignGoal.LivingInTown,
    ];

    /// <summary>Picked when the agent has not chosen yet: the type with the strongest like rate in the research.</summary>
    public const VerticalCampaignGoal Default = VerticalCampaignGoal.WhatPriceBuys;

    public static IReadOnlyList<VerticalAnnouncement> Announcements { get; } = Enum.GetValues<VerticalAnnouncement>();

    public static bool IsOffered(VerticalCampaignGoal? goal) => goal is { } value && Offered.Contains(value);

    /// <summary>The format a campaign type makes first; null for the original goals, which never picked a format.</summary>
    public static VerticalCreativeFormat? LeadFormat(VerticalCampaignGoal? goal) => goal switch
    {
        VerticalCampaignGoal.WhatPriceBuys => VerticalCreativeFormat.PriceFirst,
        VerticalCampaignGoal.GuessThePrice => VerticalCreativeFormat.PriceFirst,
        VerticalCampaignGoal.StandoutRoom => VerticalCreativeFormat.WowFeature,
        VerticalCampaignGoal.SpeedTour => VerticalCreativeFormat.SpeedTour,
        VerticalCampaignGoal.AgentHosted => VerticalCreativeFormat.HostedPov,
        VerticalCampaignGoal.LuxuryShowcase => VerticalCreativeFormat.WowFeature,
        VerticalCampaignGoal.LivingInTown => VerticalCreativeFormat.NeighborhoodTour,
        _ => null,
    };

    public static string AnnouncementName(VerticalAnnouncement announcement) => announcement switch
    {
        VerticalAnnouncement.JustListed => "Just listed",
        VerticalAnnouncement.OpenHouse => "Open house",
        VerticalAnnouncement.PriceDrop => "Price drop",
        _ => "No announcement",
    };

    /// <summary>The campaign message an announcement sets; null for none.</summary>
    public static CampaignMarketingTemplate? TemplateOf(VerticalAnnouncement announcement) => announcement switch
    {
        VerticalAnnouncement.JustListed => CampaignMarketingTemplate.JustListed,
        VerticalAnnouncement.OpenHouse => CampaignMarketingTemplate.OpenHouse,
        VerticalAnnouncement.PriceDrop => CampaignMarketingTemplate.PriceDrop,
        _ => null,
    };

    /// <summary>The announcement a stored campaign message stands for (None for every other message).</summary>
    public static VerticalAnnouncement AnnouncementOf(CampaignMarketingTemplate template) => template switch
    {
        CampaignMarketingTemplate.JustListed => VerticalAnnouncement.JustListed,
        CampaignMarketingTemplate.OpenHouse => VerticalAnnouncement.OpenHouse,
        CampaignMarketingTemplate.PriceDrop => VerticalAnnouncement.PriceDrop,
        _ => VerticalAnnouncement.None,
    };

    /// <summary>
    /// A campaign type for a goal saved before the campaign types existed, so reopening an old campaign still shows a
    /// choice. The original goal itself is kept on that campaign.
    /// </summary>
    public static VerticalCampaignGoal FromLegacy(VerticalCampaignGoal? goal) => goal switch
    {
        { } value when IsOffered(value) => value,
        VerticalCampaignGoal.MaximizeReach => VerticalCampaignGoal.StandoutRoom,
        _ => Default,
    };

    /// <summary>The research line and one example TikTok post for each campaign type.</summary>
    public static VerticalCampaignGoalEvidence? Evidence(VerticalCampaignGoal goal) => goal switch
    {
        VerticalCampaignGoal.WhatPriceBuys => new(
            "Highest like rate in our research: about 12% to 23% of viewers liked these videos.",
            "Hortons: What £635,000 buys in south Leicestershire",
            "https://www.tiktok.com/@hortons.co/video/7280574057404239136"),
        VerticalCampaignGoal.GuessThePrice => new(
            "Holding the price back draws comments. No published like rate yet.",
            "Hortons: Can you guess how much this property is listed for",
            "https://www.tiktok.com/@hortons.co/video/7310259634487446817"),
        VerticalCampaignGoal.StandoutRoom => new(
            "Curiosity drives the biggest view counts, without making fun of the home.",
            "Zillow Gone Wild: a striking listing",
            "https://www.tiktok.com/@zillowgonewild/video/7337355558858624302"),
        VerticalCampaignGoal.SpeedTour => new(
            "Speed tours have reached 1M to 34M views. No published like rate.",
            "Trent Miller: Maryland speed tour",
            "https://www.tiktok.com/@trent_miller__/video/7376239067333365038"),
        VerticalCampaignGoal.AgentHosted => new(
            "Creators with a repeatable opening get the most views overall (about 10M per video for the top series).",
            "Caleb Simpson: NYC apartment tour",
            "https://www.tiktok.com/@calebwsimpson/video/7357487981843844395"),
        VerticalCampaignGoal.LuxuryShowcase => new(
            "Luxury tours reached 13% to 31% likes per view in 2020 (unconfirmed counts).",
            "Aaron Grushow: $47.8M Malibu estate",
            "https://www.tiktok.com/@aarongrushowhomes/video/7260220075834101035"),
        VerticalCampaignGoal.LivingInTown => new(
            "Weakest evidence of the types: one team's neighborhood tours reached 61M views in total.",
            "HEIDER: Would you move to Georgetown to live here?",
            "https://www.tiktok.com/@heider_realestate/video/7633854193727474975"),
        _ => null,
    };
}
