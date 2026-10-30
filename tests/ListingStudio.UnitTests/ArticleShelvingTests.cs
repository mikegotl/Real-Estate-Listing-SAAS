using ListingStudio.Application.Articles;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class ArticleShelvingTests
{
    private static readonly DateOnly Today = new(2026, 10, 30);

    [Fact]
    public void RecentArticlesAreReleasedNewestFirst()
    {
        var older = Article("older", new DateOnly(2026, 3, 1));
        var newer = Article("newer", new DateOnly(2026, 10, 1));

        var shelves = ArticleShelving.Arrange([older, newer], Today);

        Assert.Equal(["newer", "older"], shelves.RecentlyReleased.Select(article => article.Slug));
        Assert.Empty(shelves.Archived);
    }

    [Fact]
    public void ArticlesOlderThanTheWindowAreArchived()
    {
        var edge = Article("edge", Today.AddDays(-ArticleShelving.RecentWindowDays));
        var stale = Article("stale", Today.AddDays(-ArticleShelving.RecentWindowDays - 1));

        var shelves = ArticleShelving.Arrange([stale, edge], Today);

        Assert.Equal(["edge"], shelves.RecentlyReleased.Select(article => article.Slug));
        Assert.Equal(["stale"], shelves.Archived.Select(article => article.Slug));
    }

    [Fact]
    public void FlaggedArticlesAreArchivedEvenWhenNew()
    {
        var flagged = Article("flagged", Today) with { IsArchived = true };

        var shelves = ArticleShelving.Arrange([flagged], Today);

        Assert.Empty(shelves.RecentlyReleased);
        Assert.Equal(["flagged"], shelves.Archived.Select(article => article.Slug));
    }

    [Fact]
    public void AnUpdateKeepsAnOldArticleRecent()
    {
        var refreshed = Article("refreshed", new DateOnly(2024, 1, 1)) with { UpdatedOn = new DateOnly(2026, 9, 1) };

        Assert.False(ArticleShelving.IsArchived(refreshed, Today));
        Assert.Equal(new DateOnly(2026, 9, 1), ArticleShelving.LastChangedOn(refreshed));
    }

    private static ArticleSummary Article(string slug, DateOnly publishedOn) =>
        new(slug, slug, "Description", publishedOn, null, "Editorial team", 5, ["Marketing"]);
}
