using ListingStudio.Application.Properties;
using ListingStudio.Domain.Properties;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ListingStudio.Infrastructure.Properties;

/// <summary>The photo a drone intro lands on: the first shot of the tour, when that shot is on the Exterior front stop.</summary>
internal sealed record AerialIntroFrontPhoto(Guid Id, string BlobPath);

internal static class AerialIntroSource
{
    public static async Task<AerialIntroFrontPhoto?> LoadFrontPhotoAsync(
        ApplicationDbContext dbContext,
        Guid organizationId,
        Guid propertyId,
        CancellationToken cancellationToken)
    {
        var shots = await dbContext.PropertyMedia.AsNoTracking()
            .Where(media => media.OrganizationId == organizationId
                && media.PropertyId == propertyId
                && !media.IsInGallery
                && media.WalkthroughStop != null)
            .Select(media => new { media.Id, media.WalkthroughStop, media.DisplayOrder, media.BlobPath })
            .ToArrayAsync(cancellationToken);
        var arranged = WalkthroughStoryboard.Arrange(shots, shot => shot.WalkthroughStop, shot => shot.DisplayOrder);
        var first = arranged.Count > 0 ? arranged[0] : null;
        return first is not null
            && string.Equals(first.WalkthroughStop, WalkthroughStoryboard.ExteriorFront, StringComparison.OrdinalIgnoreCase)
            ? new AerialIntroFrontPhoto(first.Id, first.BlobPath)
            : null;
    }

    /// <summary>A clip still matches the tour when it lands on today's front photo, made from today's version of it.</summary>
    public static bool IsCurrent(AerialIntroClip clip, AerialIntroFrontPhoto? front) =>
        front is not null && clip.PropertyMediaId == front.Id && clip.SourceBlobPath == front.BlobPath;
}
