using ListingStudio.Domain.Properties;
using ListingStudio.Domain.Stories;

namespace ListingStudio.Application.Stories;

public sealed record VerifiedPropertyData(
    string Address1,
    string? Address2,
    string City,
    string State,
    string ZipCode,
    decimal ListingPrice,
    int Bedrooms,
    decimal Bathrooms,
    int? SquareFeet,
    decimal? LotSize,
    int? YearBuilt,
    PropertyType PropertyType,
    string? Description,
    ListingStatus ListingStatus);

public sealed record PropertyMediaObservation(
    Guid MediaId,
    int DisplayOrder,
    PropertyMediaCategory Category,
    string RoomType,
    int QualityScore,
    int HeroScore,
    bool IsExterior,
    bool IsInterior,
    bool ContainsPeople,
    IReadOnlyList<string> PotentialProblems,
    string Description,
    int SuggestedDisplayOrder);

public sealed record PropertyStoryBranding(string OrganizationName, string? AgentName);

public sealed record ApprovedNeighborhoodFact(
    string Category,
    string Name,
    string Address,
    decimal DistanceMiles,
    string SourceUrl,
    DateTimeOffset CheckedAtUtc,
    Guid? InsightId = null);

public sealed record PropertyStoryGenerationRequest(
    VerifiedPropertyData VerifiedProperty,
    IReadOnlyList<PropertyMediaObservation> MediaObservations,
    PropertyStoryBranding Branding,
    IReadOnlyList<ApprovedNeighborhoodFact>? ApprovedNeighborhoodFacts = null,
    IReadOnlyList<WalkthroughStoryStop>? Walkthrough = null);

public sealed record PropertyStoryResult(
    Guid Id,
    Guid PropertyId,
    int Version,
    string GenerationVersion,
    PropertyStoryContent Content,
    DateTimeOffset CreatedAtUtc,
    bool Reused);

public sealed record PropertyStoryGroundingResult(
    bool IsValid,
    IReadOnlyList<string> Errors,
    IReadOnlyList<PropertyStoryUnverifiedClaim>? UnverifiedClaims = null)
{
    public static PropertyStoryGroundingResult Success { get; } = new(true, []);
}

// A feature word such as "garage" that the story used but the Description, approved neighborhood facts and Tour Map
// stop names do not confirm. SeenInPhotos says whether photo analysis noticed it, which hints but does not verify.
public sealed record PropertyStoryUnverifiedClaim(string Claim, bool SeenInPhotos);
