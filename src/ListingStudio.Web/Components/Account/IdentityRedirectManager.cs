using Microsoft.AspNetCore.Components;

namespace ListingStudio.Web.Components.Account;

internal sealed class IdentityRedirectManager(NavigationManager navigationManager)
{
    /// <summary>Where a signed-in agent lands when no return URL was requested: the workspace dashboard.</summary>
    public const string WorkspacePath = "/auth";

    /// <summary>Redirects after signing in or registering: to the requested page, or to the workspace.</summary>
    public void RedirectAfterSignIn(string? returnUrl) =>
        RedirectTo(string.IsNullOrWhiteSpace(returnUrl) ? WorkspacePath : returnUrl);

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
