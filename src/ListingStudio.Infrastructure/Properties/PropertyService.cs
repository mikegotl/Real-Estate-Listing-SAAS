using ListingStudio.Application.Properties;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Properties;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ListingStudio.Infrastructure.Properties;

public sealed class PropertyService(ApplicationDbContext dbContext) : IPropertyService
{
    public async Task<IReadOnlyList<PropertySummary>> ListAsync(
        string userId,
        bool includeArchived = false,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var query = dbContext.Properties
            .AsNoTracking()
            .Where(property => property.OrganizationId == organizationId);

        if (!includeArchived)
        {
            query = query.Where(property => property.ArchivedAtUtc == null);
        }

        return await query
            .OrderByDescending(property => property.UpdatedAtUtc)
            .Select(property => new PropertySummary(
                property.Id,
                property.Address1,
                property.City,
                property.State,
                property.ZipCode,
                property.ListingPrice,
                property.PropertyType,
                property.ListingStatus,
                property.ArchivedAtUtc != null,
                property.Bedrooms,
                property.Bathrooms,
                property.SquareFeet,
                property.UpdatedAtUtc,
                dbContext.PropertyMedia.Count(media =>
                    media.OrganizationId == organizationId && media.PropertyId == property.Id),
                dbContext.PropertyMedia.Count(media =>
                    media.OrganizationId == organizationId
                    && media.PropertyId == property.Id
                    && media.AnalysisStatus == PropertyMediaAnalysisStatus.Failed),
                dbContext.PropertyMedia
                    .Where(media => media.OrganizationId == organizationId && media.PropertyId == property.Id)
                    .OrderBy(media => media.DisplayOrder)
                    .Select(media => (Guid?)media.Id)
                    .FirstOrDefault(),
                dbContext.CampaignGenerationJobs
                    .Where(job => job.OrganizationId == organizationId && job.PropertyId == property.Id)
                    .OrderByDescending(job => job.CreatedAtUtc)
                    .Select(job => (CampaignGenerationStatus?)job.Status)
                    .FirstOrDefault()))
            .ToListAsync(cancellationToken);
    }

    public async Task<PropertyDetailsResult?> GetAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        return await dbContext.Properties
            .AsNoTracking()
            .Where(property => property.Id == propertyId && property.OrganizationId == organizationId)
            .Select(property => new PropertyDetailsResult(
                property.Id,
                property.Address1,
                property.Address2,
                property.City,
                property.State,
                property.ZipCode,
                property.ListingPrice,
                property.Bedrooms,
                property.Bathrooms,
                property.SquareFeet,
                property.LotSize,
                property.YearBuilt,
                property.PropertyType,
                property.Description,
                property.ListingStatus,
                property.ArchivedAtUtc != null,
                property.CreatedAtUtc,
                property.UpdatedAtUtc))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<Guid> CreateAsync(
        string userId,
        PropertyInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var property = ListingProperty.Create(organizationId, ToDetails(input));
        dbContext.Properties.Add(property);
        await dbContext.SaveChangesAsync(cancellationToken);
        return property.Id;
    }

    public async Task<bool> UpdateAsync(
        string userId,
        Guid propertyId,
        PropertyInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var property = await dbContext.Properties.SingleOrDefaultAsync(
            candidate => candidate.Id == propertyId && candidate.OrganizationId == organizationId,
            cancellationToken);

        if (property is null)
        {
            return false;
        }

        property.Update(ToDetails(input));
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> ArchiveAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var property = await dbContext.Properties.SingleOrDefaultAsync(
            candidate => candidate.Id == propertyId && candidate.OrganizationId == organizationId,
            cancellationToken);

        if (property is null)
        {
            return false;
        }

        property.Archive();
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

    private static PropertyDetails ToDetails(PropertyInput input) => new(
        input.Address1,
        input.Address2,
        input.City,
        input.State,
        input.ZipCode,
        input.ListingPrice,
        input.Bedrooms,
        input.Bathrooms,
        input.SquareFeet,
        input.LotSize,
        input.YearBuilt,
        input.PropertyType,
        input.Description,
        input.ListingStatus);
}
