namespace ListingStudio.Web;

/// <summary>
/// The public address of the site. Canonical and share links use it because behind the Container Apps ingress the
/// request can arrive as plain http or on the azurecontainerapps.io host. When blank, links fall back to the request.
/// </summary>
public sealed class SiteOptions
{
    public const string SectionName = "Site";

    public string PublicBaseUrl { get; init; } = string.Empty;

    public string AbsoluteUrl(string path, Uri requestBaseUri)
    {
        var baseUri = Uri.TryCreate(PublicBaseUrl, UriKind.Absolute, out var configured) ? configured : requestBaseUri;
        return new Uri(baseUri, path).ToString();
    }
}
