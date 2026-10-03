using Microsoft.AspNetCore.Components;

namespace ListingStudio.Web.Components.Account;

internal sealed class IdentityRedirectManager(NavigationManager navigationManager)
{
    public void RedirectTo(string? uri)
    {
        uri ??= string.Empty;
        if (!Uri.IsWellFormedUriString(uri, UriKind.Relative))
        {
            uri = navigationManager.ToBaseRelativePath(uri);
        }

        navigationManager.NavigateTo(uri, forceLoad: true);
    }

    public void RedirectTo(string uri, IReadOnlyDictionary<string, object?> queryParameters)
    {
        var absoluteUri = navigationManager.ToAbsoluteUri(uri).GetLeftPart(UriPartial.Path);
        RedirectTo(navigationManager.GetUriWithQueryParameters(absoluteUri, queryParameters));
    }
}
