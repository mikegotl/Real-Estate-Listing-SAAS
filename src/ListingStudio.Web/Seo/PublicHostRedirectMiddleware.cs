using Microsoft.Extensions.Options;

namespace ListingStudio.Web.Seo;

/// <summary>
/// Moves page visits from the Azure Container Apps address (and www) to the public domain, so search engines
/// index one address. Only GET and HEAD are redirected; health probes, webhooks and form posts keep working on any host.
/// </summary>
public sealed class PublicHostRedirectMiddleware(RequestDelegate next, IOptions<SiteOptions> options)
{
    public Task InvokeAsync(HttpContext context)
    {
        var site = options.Value;
        if (!site.RedirectToPublicHost
            || !(HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
            || context.Request.Path.StartsWithSegments("/health")
            || !Uri.TryCreate(site.PublicBaseUrl, UriKind.Absolute, out var publicBase))
        {
            return next(context);
        }

        var host = context.Request.Host.Host;
        if (string.Equals(host, publicBase.Host, StringComparison.OrdinalIgnoreCase)
            || !site.RedirectHosts.Any(candidate => Matches(host, candidate)))
        {
            return next(context);
        }

        var target = new UriBuilder(publicBase)
        {
            Path = context.Request.PathBase.Add(context.Request.Path).Value ?? "/",
            Query = context.Request.QueryString.HasValue ? context.Request.QueryString.Value![1..] : string.Empty,
        };
        context.Response.Redirect(target.Uri.AbsoluteUri, permanent: true);
        return Task.CompletedTask;
    }

    internal static bool Matches(string host, string candidate) =>
        !string.IsNullOrWhiteSpace(candidate)
        && (candidate.StartsWith('.')
            ? host.EndsWith(candidate, StringComparison.OrdinalIgnoreCase)
            : string.Equals(host, candidate, StringComparison.OrdinalIgnoreCase));
}
