using ListingStudio.Infrastructure.Identity;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class AgentProfileTests
{
    [Fact]
    public void AvatarCampaignDefaultCanBeEnabledAndDisabled()
    {
        var user = new ApplicationUser();

        user.SetAvatarCampaignDefault(true);
        Assert.True(user.IncludeAvatarInCampaigns);

        user.SetAvatarCampaignDefault(false);
        Assert.False(user.IncludeAvatarInCampaigns);
    }
}
