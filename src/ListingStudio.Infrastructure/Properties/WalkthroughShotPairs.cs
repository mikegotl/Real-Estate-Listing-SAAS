using System.Security.Cryptography;
using System.Text;
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
            .Where(media => media.OrganizationId == organizationId
                && media.PropertyId == propertyId
                && !media.IsInGallery
                && media.WalkthroughStop != null)
            .Select(media => new { media.Id, media.WalkthroughStop, media.DisplayOrder, media.BlobPath, media.OriginalBlobPath })
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
                var photos = pair.From.OriginalBlobPath is null && pair.To.OriginalBlobPath is null
                    ? string.Empty
                    : PhotoVersion(pair.From.BlobPath, pair.To.BlobPath);
                return new WalkthroughShotPair(
                    pair.From.Id,
                    pair.To.Id,
                    direction,
                    WalkthroughClipDirection.Version(provider.GenerationVersion + photos, direction));
            })
            .ToArray();
    }

    /// <summary>
    /// A clip between fixed photos is tied to the photo files it was made from, so fixing or reverting either photo
    /// makes it out of date. Pairs of unfixed photos keep their plain version, and reverting both photos brings back
    /// the clip made from the originals.
    /// </summary>
    internal static string PhotoVersion(string fromBlobPath, string toBlobPath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{fromBlobPath}\n{toBlobPath}"));
        return $"/img-{Convert.ToHexStringLower(hash.AsSpan(0, 4))}";
    }
}
