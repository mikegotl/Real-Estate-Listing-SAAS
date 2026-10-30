using Microsoft.AspNetCore.Identity;

namespace ListingStudio.Infrastructure.Identity;

public sealed class ApplicationUser : IdentityUser
{
    public DateTimeOffset? LastSuccessfulSignInAtUtc { get; private set; }

    public bool IncludeAvatarInCampaigns { get; private set; }

    public bool RequiresPasswordChange { get; private set; }

    /// <summary>A platform admin's raised monthly campaign limit for this tester; null uses the default.</summary>
    public int? TesterMonthlyCampaignLimit { get; private set; }

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

    public void SetTesterMonthlyCampaignLimit(int? limit)
    {
        if (limit is < 0 or > ListingStudio.Application.Administration.PlatformTesters.MaximumMonthlyCampaignLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        TesterMonthlyCampaignLimit = limit;
    }

    public void RequirePasswordChange()
    {
        RequiresPasswordChange = true;
    }

    public void CompleteRequiredPasswordChange()
    {
        RequiresPasswordChange = false;
    }
}
