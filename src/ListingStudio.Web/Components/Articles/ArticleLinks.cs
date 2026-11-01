namespace ListingStudio.Web.Components.Articles;

/// <summary>
/// Links to articles from inside the product. Each one goes through /go/article/{slug}, which counts the click by
/// article and placement only (never a property, campaign or user) and then redirects.
/// </summary>
public static class ArticleLinks
{
    public static class Placements
    {
        public const string ArticleCta = "article-cta";
        public const string DeliverableSelector = "campaign-selector";
        public const string Recommendations = "recommendations";
        public const string ReviewGallery = "library-gallery";
        public const string AdminStudio = "admin-creative";
        public const string HelpChat = "help-chat";

        public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
        {
            ArticleCta, DeliverableSelector, Recommendations, ReviewGallery, AdminStudio, HelpChat,
        };
    }

    /// <summary>Where a tracked link may land besides the article itself. Anything else goes to the article.</summary>
    public static IReadOnlySet<string> AllowedTargets { get; } = new HashSet<string>(StringComparer.Ordinal) { "/Account/Register" };

    public static string Track(string slug, string placement, string? target = null) =>
        $"/go/article/{Uri.EscapeDataString(slug)}?from={Uri.EscapeDataString(placement)}"
        + (target is null ? string.Empty : $"&to={Uri.EscapeDataString(target)}");
}
