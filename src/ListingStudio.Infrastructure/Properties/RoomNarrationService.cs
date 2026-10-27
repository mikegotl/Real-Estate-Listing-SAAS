using ListingStudio.Application.Properties;
using ListingStudio.Domain.Properties;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ListingStudio.Infrastructure.Properties;

public sealed class RoomNarrationService(ApplicationDbContext dbContext, TimeProvider timeProvider) : IRoomNarrationService
{
    public async Task<RoomNarrationOverview> GetAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var choice = await dbContext.Properties
            .AsNoTracking()
            .Where(property => property.OrganizationId == organizationId && property.Id == propertyId)
            .Select(property => new { property.UseRoomNarration })
            .SingleOrDefaultAsync(cancellationToken);
        if (choice is null)
        {
            return RoomNarrationOverview.Empty;
        }

        var narrations = await dbContext.WalkthroughStopNarrations
            .AsNoTracking()
            .Where(narration => narration.OrganizationId == organizationId && narration.PropertyId == propertyId)
            .OrderBy(narration => narration.Stop)
            .Select(narration => new RoomNarrationItem(narration.Stop, narration.Text, narration.UpdatedAtUtc))
            .ToListAsync(cancellationToken);
        var (segments, _) = await UserNarrationSource.LoadRoomNarrationAsync(
            dbContext,
            organizationId,
            propertyId,
            cancellationToken);
        var narratedStops = segments
            .Select(segment => segment.WalkthroughStop)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        return new RoomNarrationOverview(
            narrations,
            choice.UseRoomNarration,
            narratedStops,
            NarrationScriptPolicy.CountWords(RoomNarrationScript.FullText(segments)));
    }

    public async Task<bool> SetAsync(
        string userId,
        Guid propertyId,
        string walkthroughStop,
        string? text,
        CancellationToken cancellationToken = default)
    {
        var stop = WalkthroughStoryboard.Normalize(walkthroughStop)
            ?? throw new ArgumentException("A walkthrough stop is required.", nameof(walkthroughStop));
        var normalizedText = WalkthroughStopNarration.Normalize(text);
        if (normalizedText is { Length: > WalkthroughStopNarration.MaximumTextLength })
        {
            throw new ArgumentOutOfRangeException(
                nameof(text),
                $"Room narration cannot exceed {WalkthroughStopNarration.MaximumTextLength} characters.");
        }

        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        if (!await ActivePropertyExistsAsync(organizationId, propertyId, cancellationToken))
        {
            return false;
        }

        // Stops match case-insensitively, like the storyboard groups them.
        var existing = (await dbContext.WalkthroughStopNarrations
                .Where(candidate => candidate.OrganizationId == organizationId && candidate.PropertyId == propertyId)
                .ToListAsync(cancellationToken))
            .Where(candidate => string.Equals(candidate.Stop, stop, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var now = timeProvider.GetUtcNow();
        if (normalizedText is null)
        {
            dbContext.WalkthroughStopNarrations.RemoveRange(existing);
        }
        else if (existing.Count == 0)
        {
            dbContext.WalkthroughStopNarrations.Add(WalkthroughStopNarration.Create(
                organizationId,
                propertyId,
                stop,
                normalizedText,
                userId,
                now));
        }
        else
        {
            existing[0].SetText(normalizedText, userId, now);
            dbContext.WalkthroughStopNarrations.RemoveRange(existing.Skip(1));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> ChooseSourceAsync(
        string userId,
        Guid propertyId,
        bool useRoomNarration,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var property = await dbContext.Properties
            .SingleOrDefaultAsync(
                candidate => candidate.Id == propertyId
                    && candidate.OrganizationId == organizationId
                    && candidate.ArchivedAtUtc == null,
                cancellationToken);
        if (property is null)
        {
            return false;
        }

        property.ChooseNarrationSource(useRoomNarration, timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private Task<bool> ActivePropertyExistsAsync(Guid organizationId, Guid propertyId, CancellationToken cancellationToken) =>
        dbContext.Properties.AnyAsync(
            property => property.Id == propertyId
                && property.OrganizationId == organizationId
                && property.ArchivedAtUtc == null,
            cancellationToken);

    private async Task<Guid> GetOrganizationIdAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var organizationId = await dbContext.OrganizationMembers
            .AsNoTracking()
            .Where(member => member.UserId == userId)
            .Select(member => (Guid?)member.OrganizationId)
            .FirstOrDefaultAsync(cancellationToken);
        return organizationId ?? throw new UnauthorizedAccessException("The user does not belong to an organization.");
    }
}
