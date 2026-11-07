using ListingStudio.Application.Articles;
using ListingStudio.Web.Components.Articles.Content;

namespace ListingStudio.Web.Components.Articles;

/// <summary>A published article: its listing details plus the component that renders its body.</summary>
public sealed record ArticleEntry(ArticleSummary Summary, Type Body);

/// <summary>
/// The articles shown on the public Articles page. Each body is a Razor component in Components/Articles/Content, so an
/// article ships, is reviewed and is versioned like any other page. To retire one, set IsArchived instead of deleting it,
/// so links people shared keep working.
/// </summary>
public static class ArticleLibrary
{
    public const string Publisher = "Creative Listing Studio";

    public static IReadOnlyList<ArticleEntry> All { get; } =
    [
        new(
            new ArticleSummary(
                Slug: SocialCampaignsForAgents.Slug,
                Title: "Creative social media campaigns for every listing, from one studio",
                Description: "Why real estate agents are turning listing photos into short videos, voiceovers and posts from one place, what the latest research says about buyers and social media, and how an all-in-one studio compares with separate subscriptions.",
                PublishedOn: new DateOnly(2026, 10, 30),
                UpdatedOn: new DateOnly(2026, 11, 7),
                Author: "Creative Listing Studio team",
                ReadMinutes: 8,
                Topics: ["Social media", "Listing video", "Marketing costs"]),
            typeof(SocialCampaignsForAgents)),
        new(
            new ArticleSummary(
                Slug: OneListingFourTikToks.Slug,
                Title: "One Listing Four TikToks",
                Description: "Learn how Listing Studio analyzes verified property facts and media to recommend purpose-built vertical videos for reach engagement and buyer interest.",
                PublishedOn: new DateOnly(2026, 11, 1),
                UpdatedOn: null,
                Author: "Creative Listing Studio team",
                ReadMinutes: 6,
                Topics: ["Vertical video", "TikTok", "Listing video"],
                FeatureKey: ListingStudio.Domain.Administration.FeatureFlagKeys.VerticalVideoLibraryV1),
            typeof(OneListingFourTikToks)),
        new(
            new ArticleSummary(
                Slug: TikTokHomeTourTypes.Slug,
                Title: "Which TikTok home tours get the most views and likes",
                Description: "Real estate TikTok campaign types compared: shock homes, creator series, speed tours, price surprise, luxury, comedy and neighborhood tours, with example posts and how their like rates compare.",
                PublishedOn: new DateOnly(2026, 11, 7),
                UpdatedOn: null,
                Author: "Creative Listing Studio team",
                ReadMinutes: 9,
                Topics: ["TikTok", "Research", "Listing video"]),
            typeof(TikTokHomeTourTypes)),
    ];

    /// <summary>The vertical video library guide: shown only while VerticalVideoLibraryV1 is on (see <see cref="ArticleVisibility"/>).</summary>
    public static ArticleEntry VerticalLibraryGuide => Find(OneListingFourTikToks.Slug)!;

    public static ArticleEntry? Find(string? slug) =>
        All.FirstOrDefault(entry => string.Equals(entry.Summary.Slug, slug, StringComparison.OrdinalIgnoreCase));
}
