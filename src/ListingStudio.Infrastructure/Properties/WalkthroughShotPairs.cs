using ListingStudio.Application.Properties;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Properties;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ListingStudio.Infrastructure.Properties;

/// <summary>A consecutive storyboard shot pair and the generation version its clip must have to be current.</summary>
internal sealed record WalkthroughShotPair(Guid From, Guid To, WalkthroughDirection? Direction, string Version);

internal static class WalkthroughShotPairs
{
    /// <summary>
    /// The property's consecutive shot pairs in storyboard order, each with the walking direction from the
    /// walkthrough map and the clip version that direction calls for.
    /// </summary>
    public static async Task<IReadOnlyList<WalkthroughShotPair>> LoadAsync(
        ApplicationDbContext dbContext,
        IWalkthroughVideoProvider provider,
        Guid organizationId,
        Guid propertyId,
        CancellationToken cancellationToken)
    {
        var shots = await dbContext.PropertyMedia.AsNoTracking()
            .Where(media => media.OrganizationId == organizationId && media.PropertyId == propertyId)
            .Select(media => new { media.Id, media.WalkthroughStop, media.DisplayOrder })
            .ToArrayAsync(cancellationToken);
        var directions = (await dbContext.WalkthroughStopDirections.AsNoTracking()
                .Where(note => note.OrganizationId == organizationId && note.PropertyId == propertyId)
                .Select(note => new { note.Stop, note.Direction })
                .ToArrayAsync(cancellationToken))
            .GroupBy(note => note.Stop, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Direction, StringComparer.OrdinalIgnoreCase);
        return WalkthroughStoryboard.ShotPairs(shots, shot => shot.WalkthroughStop, shot => shot.DisplayOrder)
            .Select(pair =>
            {
                var direction = WalkthroughClipDirection.ForPair(
                    pair.From.WalkthroughStop,
                    pair.To.WalkthroughStop,
                    directions);
                return new WalkthroughShotPair(
                    pair.From.Id,
                    pair.To.Id,
                    direction,
                    WalkthroughClipDirection.Version(provider.GenerationVersion, direction));
            })
            .ToArray();
    }
}
