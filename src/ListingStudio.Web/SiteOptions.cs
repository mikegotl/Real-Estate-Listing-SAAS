namespace ListingStudio.Web;

/// <summary>
/// The public address of the site. Canonical and share links use it because behind the Container Apps ingress the
/// request can arrive as plain http or on the azurecontainerapps.io host. When blank, links fall back to the request.
/// </summary>
public sealed class SiteOptions
{
    public const string SectionName = "Site";

    public string PublicBaseUrl { get; init; } = string.Empty;

    /// <summary>Sends page visits on <see cref="RedirectHosts"/> to <see cref="PublicBaseUrl"/> with a permanent redirect.</summary>
    public bool RedirectToPublicHost { get; init; }

    /// <summary>Exact host names, or suffixes starting with a dot such as ".azurecontainerapps.io".</summary>
    public string[] RedirectHosts { get; init; } = [];

    public string AbsoluteUrl(string path, Uri requestBaseUri)
    {
        var baseUri = Uri.TryCreate(PublicBaseUrl, UriKind.Absolute, out var configured) ? configured : requestBaseUri;
        return new Uri(baseUri, path).ToString();
    }
}
