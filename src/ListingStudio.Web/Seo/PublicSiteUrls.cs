using Microsoft.Extensions.Options;

namespace ListingStudio.Web.Seo;

public sealed class PublicSiteUrls(IOptions<SiteOptions> options)
{
    /// <summary>The configured public origin, or <paramref name="fallbackBaseUri"/> when none is set.</summary>
    public Uri BaseUri(string fallbackBaseUri)
    {
        var configured = options.Value.PublicBaseUrl;
        if (Uri.TryCreate(configured, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
        }

        return new Uri(new Uri(fallbackBaseUri).GetLeftPart(UriPartial.Authority) + "/");
    }

    public string Absolute(string path, string fallbackBaseUri) =>
        new Uri(BaseUri(fallbackBaseUri), path.TrimStart('/')).AbsoluteUri;

    public static string RequestBaseUri(HttpRequest request) => $"{request.Scheme}://{request.Host}/";
}
