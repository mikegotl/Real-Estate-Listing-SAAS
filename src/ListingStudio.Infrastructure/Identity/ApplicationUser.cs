using Microsoft.AspNetCore.Identity;

namespace ListingStudio.Infrastructure.Identity;

public sealed class ApplicationUser : IdentityUser
{
    public DateTimeOffset? LastSuccessfulSignInAtUtc { get; private set; }

    public bool IncludeAvatarInCampaigns { get; private set; }

    public void RecordSuccessfulSignIn(DateTimeOffset occurredAtUtc)
    {
        if (LastSuccessfulSignInAtUtc is null || occurredAtUtc > LastSuccessfulSignInAtUtc)
        {
            LastSuccessfulSignInAtUtc = occurredAtUtc;
        }
    }

    public void SetAvatarCampaignDefault(bool includeAvatarInCampaigns)
    {
        IncludeAvatarInCampaigns = includeAvatarInCampaigns;
    }
}
