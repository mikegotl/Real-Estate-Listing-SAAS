using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ListingStudio.Application.Stories;
using ListingStudio.Domain.Properties;
using ListingStudio.Domain.Stories;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using ListingStudio.Application.Neighborhoods;

namespace ListingStudio.Infrastructure.Stories;

public sealed class PropertyStoryService(
    ApplicationDbContext dbContext,
    IPropertyStoryGenerator generator,
    IPropertyStoryGroundingValidator groundingValidator) : IPropertyStoryService
{
    private static readonly JsonSerializerOptions FingerprintSerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<PropertyStoryResult?> GetLatestAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var story = await dbContext.PropertyStories
            .AsNoTracking()
            .Where(candidate => candidate.OrganizationId == organizationId && candidate.PropertyId == propertyId)
            .OrderByDescending(candidate => candidate.Version)
            .FirstOrDefaultAsync(cancellationToken);
        return story is null ? null : ToResult(story, reused: false);
    }

    public async Task<PropertyStoryResult?> GenerateAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var property = await dbContext.Properties
            .AsNoTracking()
            .Include(candidate => candidate.Media)
            .SingleOrDefaultAsync(
                candidate => candidate.Id == propertyId && candidate.OrganizationId == organizationId,
                cancellationToken);
        if (property is null)
        {
            return null;
        }

        if (property.IsArchived)
        {
            throw new InvalidOperationException("Archived properties cannot generate marketing stories.");
        }

        if (property.Media.Count == 0)
        {
            throw new InvalidOperationException("At least one analyzed property image is required to generate a story.");
        }

        if (property.Media.Any(media => media.AnalysisStatus != PropertyMediaAnalysisStatus.Completed))
        {
            throw new InvalidOperationException("Every property image must have completed analysis before story generation.");
        }

        var request = await CreateRequestAsync(organizationId, property, cancellationToken);
        var fingerprint = CreateFingerprint(generator.GenerationVersion, request);
        var existing = await FindByFingerprintAsync(organizationId, propertyId, fingerprint, cancellationToken);
        if (existing is not null)
        {
            return ToResult(existing, reused: true);
        }

        var content = await generator.GenerateAsync(request, cancellationToken);
        var grounding = groundingValidator.Validate(request, content);
        if (!grounding.IsValid)
        {
            throw new InvalidDataException(
                $"The story could not be saved because it contains unverified property information. "
                + string.Join(' ', grounding.Errors));
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        existing = await FindByFingerprintAsync(organizationId, propertyId, fingerprint, cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return ToResult(existing, reused: true);
        }

        var latestVersion = await dbContext.PropertyStories
            .Where(candidate => candidate.OrganizationId == organizationId && candidate.PropertyId == propertyId)
            .Select(candidate => (int?)candidate.Version)
            .MaxAsync(cancellationToken) ?? 0;
        var story = PropertyStory.Create(
            organizationId,
            propertyId,
            latestVersion + 1,
            generator.GenerationVersion,
            fingerprint,
            content);
        dbContext.PropertyStories.Add(story);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToResult(story, reused: false);
    }

    private async Task<PropertyStoryGenerationRequest> CreateRequestAsync(
        Guid organizationId,
        ListingProperty property,
        CancellationToken cancellationToken)
    {
        var branding = await dbContext.Organizations
            .AsNoTracking()
            .Where(organization => organization.Id == organizationId)
            .Select(organization => new PropertyStoryBranding(organization.Name, null))
            .SingleAsync(cancellationToken);

        var verified = new VerifiedPropertyData(
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
            property.ListingStatus);
        var observations = property.Media
            .OrderBy(media => media.DisplayOrder)
            .Select(media => ToObservation(media, media.GetAnalysis()!))
            .ToArray();
        var neighborhoodFacts = await dbContext.NeighborhoodInsights
            .AsNoTracking()
            .Where(insight => insight.OrganizationId == organizationId
                && insight.PropertyId == property.Id
                && insight.IsApproved)
            .OrderBy(insight => insight.Category)
            .ThenBy(insight => insight.DistanceMiles)
            .Select(insight => new ApprovedNeighborhoodFact(
                insight.Category.ToString(),
                insight.Name,
                insight.Address,
                insight.DistanceMiles,
                insight.SourceUrl,
                insight.CheckedAtUtc))
            .ToArrayAsync(cancellationToken);
        return new PropertyStoryGenerationRequest(verified, observations, branding, neighborhoodFacts);
    }

    private static PropertyMediaObservation ToObservation(
        PropertyMedia media,
        PropertyMediaAnalysis analysis) => new(
            media.Id,
            media.DisplayOrder,
            analysis.Category,
            analysis.RoomType,
            analysis.QualityScore,
            analysis.HeroScore,
            analysis.IsExterior,
            analysis.IsInterior,
            analysis.ContainsPeople,
            analysis.PotentialProblems,
            analysis.Description,
            analysis.SuggestedDisplayOrder);

    private Task<PropertyStory?> FindByFingerprintAsync(
        Guid organizationId,
        Guid propertyId,
        string fingerprint,
        CancellationToken cancellationToken) => dbContext.PropertyStories
        .AsNoTracking()
        .SingleOrDefaultAsync(
            story => story.OrganizationId == organizationId
                && story.PropertyId == propertyId
                && story.SourceFingerprint == fingerprint,
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

    private static string CreateFingerprint(
        string generationVersion,
        PropertyStoryGenerationRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generationVersion);
        var source = JsonSerializer.Serialize(new { generationVersion, request }, FingerprintSerializerOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
    }

    private static PropertyStoryResult ToResult(PropertyStory story, bool reused) => new(
        story.Id,
        story.PropertyId,
        story.Version,
        story.GenerationVersion,
        story.GetContent(),
        story.CreatedAtUtc,
        reused);
}
