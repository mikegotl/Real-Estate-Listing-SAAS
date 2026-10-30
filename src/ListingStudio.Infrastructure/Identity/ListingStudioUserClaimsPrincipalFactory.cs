using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace ListingStudio.Infrastructure.Identity;

public sealed class ListingStudioUserClaimsPrincipalFactory(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole> roleManager,
    IOptions<IdentityOptions> optionsAccessor)
    : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole>(userManager, roleManager, optionsAccessor)
{
    public const string RequiresPasswordChangeClaim = "creative-listing-studio:requires-password-change";

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        if (user.RequiresPasswordChange)
        {
            identity.AddClaim(new Claim(RequiresPasswordChangeClaim, bool.TrueString));
        }

        return identity;
    }
}
