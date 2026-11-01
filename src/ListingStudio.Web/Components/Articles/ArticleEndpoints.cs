using ListingStudio.Application.Administration;

namespace ListingStudio.Web.Components.Articles;

public static class ArticleEndpoints
{
    public const string ClickKind = "article-link";

    /// <summary>
    /// GET /go/article/{slug}?from={placement}[&amp;to={target}]: counts one click (article slug and placement only, no
    /// property, campaign, user or address) and redirects to the article, or to an allowed target such as registration.
    /// An unknown article, or a feature article that is off for this visitor, goes to the Articles page and counts nothing.
    /// </summary>
    public static IEndpointRouteBuilder MapArticleEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/go/article/{slug}", async (
            string slug,
            string? from,
            string? to,
            HttpContext context,
            ArticleVisibility visibility,
            IOperationalEventRecorder recorder,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var entry = ArticleLibrary.Find(slug);
            if (entry is null || !await visibility.CanSeeAsync(entry, context.User, cancellationToken))
            {
                return Results.LocalRedirect("/articles");
            }

            var placement = from is not null && ArticleLinks.Placements.All.Contains(from) ? from : "other";
            await recorder.RecordAsync(new OperationalObservation(ClickKind, entry.Summary.Slug, placement, timeProvider.GetUtcNow()),
                cancellationToken);
            return Results.LocalRedirect(to is not null && ArticleLinks.AllowedTargets.Contains(to) ? to : $"/articles/{entry.Summary.Slug}");
        }).AllowAnonymous();

        return app;
    }
}
