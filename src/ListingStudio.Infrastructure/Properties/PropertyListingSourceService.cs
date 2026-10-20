using ListingStudio.Application.Properties;
using ListingStudio.Domain.Properties;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ListingStudio.Infrastructure.Properties;

public sealed class PropertyListingSourceService(ApplicationDbContext dbContext) : IPropertyListingSourceService
{
    public async Task<PropertyListingSourceItem?> GetAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        return await dbContext.PropertyListingSources
            .AsNoTracking()
            .Where(source => source.OrganizationId == organizationId && source.PropertyId == propertyId)
            .Select(source => ToItem(source))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<PropertyListingSourceItem?> LinkZillowAsync(
        string userId,
        Guid propertyId,
        string sourceUrl,
        bool addressConfirmed,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        if (!addressConfirmed)
        {
            throw new InvalidOperationException("Confirm that the Zillow page is for this property before saving it.");
        }

        var link = ZillowListingLink.Parse(sourceUrl);
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var property = await dbContext.Properties
            .AsNoTracking()
            .Where(
            property => property.Id == propertyId
                && property.OrganizationId == organizationId
                && property.ArchivedAtUtc == null)
            .Select(property => new { property.Address1, property.ZipCode })
            .SingleOrDefaultAsync(cancellationToken);
        if (property is null)
        {
            return null;
        }

        if (!link.AppearsToMatch(property.Address1, property.ZipCode))
        {
            throw new InvalidOperationException(
                "The Zillow link does not appear to match this property's street address and ZIP code. Open the matching home's details page and copy its full link.");
        }

        var source = await dbContext.PropertyListingSources.SingleOrDefaultAsync(
            candidate => candidate.OrganizationId == organizationId
                && candidate.PropertyId == propertyId
                && candidate.Provider == PropertyListingProvider.Zillow,
            cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (source is null)
        {
            source = PropertyListingSource.Create(
                organizationId,
                propertyId,
                PropertyListingProvider.Zillow,
                link.Url,
                link.ZillowPropertyId,
                userId,
                now);
            dbContext.PropertyListingSources.Add(source);
        }
        else
        {
            source.Update(link.Url, link.ZillowPropertyId, userId, now);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return ToItem(source);
    }

    public async Task<bool> RemoveAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var source = await dbContext.PropertyListingSources.SingleOrDefaultAsync(
            candidate => candidate.OrganizationId == organizationId
                && candidate.PropertyId == propertyId
                && candidate.Property.ArchivedAtUtc == null,
            cancellationToken);
        if (source is null)
        {
            return false;
        }

        dbContext.PropertyListingSources.Remove(source);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

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

    private static PropertyListingSourceItem ToItem(PropertyListingSource source) => new(
        source.Id,
        source.Provider,
        source.SourceUrl,
        source.ExternalListingId,
        source.AddressConfirmedAtUtc,
        source.UpdatedAtUtc);
}
