using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace ListingStudio.Web.Components.Properties;

public static class UserIdentity
{
    public static async Task<string> GetRequiredUserIdAsync(AuthenticationStateProvider authenticationStateProvider)
    {
        var state = await authenticationStateProvider.GetAuthenticationStateAsync();
        return GetRequiredUserId(state.User);
    }

    public static string GetRequiredUserId(ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new UnauthorizedAccessException("An authenticated user is required.");
}
