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
                Description: "Why real estate agents are turning listing photos into short videos, voiceovers and posts from one place, what the latest research says about buyers and social media, and how much an all-in-one studio saves over separate subscriptions.",
                PublishedOn: new DateOnly(2026, 10, 30),
                UpdatedOn: null,
                Author: "Creative Listing Studio team",
                ReadMinutes: 8,
                Topics: ["Social media", "Listing video", "Marketing costs"]),
            typeof(SocialCampaignsForAgents)),
    ];

    public static ArticleEntry? Find(string? slug) =>
        All.FirstOrDefault(entry => string.Equals(entry.Summary.Slug, slug, StringComparison.OrdinalIgnoreCase));
}
