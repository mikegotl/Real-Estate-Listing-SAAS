using System.Globalization;
using System.Text;
using System.Xml;
using ListingStudio.Application.Leads;
using ListingStudio.Web.Components.Articles;

namespace ListingStudio.Web.Seo;

public static class SeoEndpoints
{
    /// <summary>Marketing pages that are always in the sitemap, with the path they are indexed under.</summary>
    public static readonly IReadOnlyList<string> PublicPages = ["/", "/articles", "/Account/Register", "/privacy"];

    // App pages sit behind sign-in, and the account pages have nothing worth ranking. Listing media is served
    // to the listing page itself and is not useful as a search result.
    private static readonly string[] DisallowedPaths =
    [
        "/Account/Login",
        "/Account/ForgotPassword",
        "/Account/ResetPassword",
        "/Account/ChangeTemporaryPassword",
        "/admin",
        "/auth",
        "/billing",
        "/leads",
        "/profile",
        "/properties",
        "/public/",
        "/status/",
    ];

    public static IEndpointRouteBuilder MapSeoEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/robots.txt", (HttpContext context, PublicSiteUrls urls) =>
        {
            var builder = new StringBuilder("User-agent: *\n");
            foreach (var path in DisallowedPaths)
            {
                builder.Append("Disallow: ").Append(path).Append('\n');
            }

            builder.Append("\nSitemap: ")
                .Append(urls.Absolute("/sitemap.xml", PublicSiteUrls.RequestBaseUri(context.Request)))
                .Append('\n');
            return Results.Text(builder.ToString(), "text/plain", Encoding.UTF8);
        }).AllowAnonymous();

        app.MapGet("/sitemap.xml", async (
            HttpContext context,
            PublicSiteUrls urls,
            ILeadGenerationService leads,
            CancellationToken cancellationToken) =>
        {
            var fallback = PublicSiteUrls.RequestBaseUri(context.Request);
            var listings = await leads.ListPublishedListingsAsync(cancellationToken);
            var entries = PublicPages
                .Select(path => (Url: urls.Absolute(path, fallback), LastModified: (DateTimeOffset?)null))
                .Concat(ArticleLibrary.All.Select(article => (
                    Url: urls.Absolute($"/articles/{article.Summary.Slug}", fallback),
                    LastModified: (DateTimeOffset?)new DateTimeOffset(
                        (article.Summary.UpdatedOn ?? article.Summary.PublishedOn).ToDateTime(TimeOnly.MinValue),
                        TimeSpan.Zero))))
                .Concat(listings.Select(listing => (
                    Url: urls.Absolute($"/listing/{listing.PublicSlug}", fallback),
                    LastModified: (DateTimeOffset?)listing.LastModifiedUtc)));
            return Results.Text(BuildSitemap(entries), "application/xml", Encoding.UTF8);
        }).AllowAnonymous();

        return app;
    }

    internal static string BuildSitemap(IEnumerable<(string Url, DateTimeOffset? LastModified)> entries)
    {
        var output = new StringBuilder();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8 }))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("urlset", "http://www.sitemaps.org/schemas/sitemap/0.9");
            foreach (var (url, lastModified) in entries)
            {
                writer.WriteStartElement("url");
                writer.WriteElementString("loc", url);
                if (lastModified is { } modified)
                {
                    writer.WriteElementString(
                        "lastmod",
                        modified.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                }

                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        // StringBuilder output is UTF-16; the response is sent as UTF-8, so the declaration must say so.
        return output.ToString().Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"", StringComparison.Ordinal);
    }
}
