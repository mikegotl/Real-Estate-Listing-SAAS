namespace ListingStudio.Application.Properties;

/// <summary>
/// Clears a property's uploaded media so the agent can start its media workspace again without removing the property.
/// </summary>
public interface IPropertyMediaResetService
{
    Task<PropertyMediaResetResult> ResetAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default);
}

public sealed record PropertyMediaResetResult(int DeletedPhotoCount, int DeletedVideoCount)
{
    public int DeletedItemCount => DeletedPhotoCount + DeletedVideoCount;
}
