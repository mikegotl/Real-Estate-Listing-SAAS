using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Properties;

namespace ListingStudio.Application.Properties;

public sealed record PropertyInput(
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

public sealed record PropertySummary(
    Guid Id,
    string Address1,
    string City,
    string State,
    string ZipCode,
    decimal ListingPrice,
    PropertyType PropertyType,
    ListingStatus ListingStatus,
    bool IsArchived,
    int Bedrooms,
    decimal Bathrooms,
    int? SquareFeet,
    DateTimeOffset UpdatedAtUtc,
    int PhotoCount,
    int FailedAnalysisCount,
    Guid? CoverMediaId,
    CampaignGenerationStatus? LatestCampaignStatus);

public sealed record PropertyDetailsResult(
    Guid Id,
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
    ListingStatus ListingStatus,
    bool IsArchived,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
