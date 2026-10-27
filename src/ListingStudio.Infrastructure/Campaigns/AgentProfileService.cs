using ListingStudio.Application.Campaigns;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ListingStudio.Infrastructure.Campaigns;

public sealed class AgentProfileService(ApplicationDbContext dbContext) : IAgentProfileService
{
    public async Task<AgentProfilePreferences> GetAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var preference = await dbContext.Users.AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => (bool?)user.IncludeAvatarInCampaigns)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new UnauthorizedAccessException("The signed-in account is unavailable.");
        return new AgentProfilePreferences(preference);
    }

    public async Task<AgentProfilePreferences> SetAvatarDefaultAsync(
        string userId,
        bool includeAvatarInCampaigns,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        if (includeAvatarInCampaigns)
        {
            var hasPortrait = await dbContext.AgentPortraits.AsNoTracking()
                .AnyAsync(portrait => portrait.UserId == userId, cancellationToken);
            if (!hasPortrait)
            {
                throw new InvalidOperationException("Upload a headshot before enabling the agent presenter default.");
            }
        }

        var user = await dbContext.Users.SingleOrDefaultAsync(
            candidate => candidate.Id == userId,
            cancellationToken)
            ?? throw new UnauthorizedAccessException("The signed-in account is unavailable.");
        user.SetAvatarCampaignDefault(includeAvatarInCampaigns);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new AgentProfilePreferences(user.IncludeAvatarInCampaigns);
    }
}
