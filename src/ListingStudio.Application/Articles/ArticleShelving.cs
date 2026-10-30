namespace ListingStudio.Application.Articles;

/// <summary>What the Articles pages show about an article: everything except its body.</summary>
public sealed record ArticleSummary(
    string Slug,
    string Title,
    string Description,
    DateOnly PublishedOn,
    DateOnly? UpdatedOn,
    string Author,
    int ReadMinutes,
    IReadOnlyList<string> Topics,
    bool IsArchived = false);

public sealed record ArticleShelves(
    IReadOnlyList<ArticleSummary> RecentlyReleased,
    IReadOnlyList<ArticleSummary> Archived);

/// <summary>
/// Splits articles into "Recently released" and "Archived". An article moves to the archive when an editor flags it
/// or when it was last published or updated more than <see cref="RecentWindowDays"/> days ago. Both shelves are newest first.
/// </summary>
public static class ArticleShelving
{
    public const int RecentWindowDays = 365;

    public static ArticleShelves Arrange(IEnumerable<ArticleSummary> articles, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(articles);

        var newestFirst = articles
            .OrderByDescending(LastChangedOn)
            .ThenBy(article => article.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ArticleShelves(
            newestFirst.Where(article => !IsArchived(article, today)).ToList(),
            newestFirst.Where(article => IsArchived(article, today)).ToList());
    }

    public static bool IsArchived(ArticleSummary article, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(article);
        return article.IsArchived || LastChangedOn(article) < today.AddDays(-RecentWindowDays);
    }

    public static DateOnly LastChangedOn(ArticleSummary article) =>
        article.UpdatedOn is { } updated && updated > article.PublishedOn ? updated : article.PublishedOn;
}
