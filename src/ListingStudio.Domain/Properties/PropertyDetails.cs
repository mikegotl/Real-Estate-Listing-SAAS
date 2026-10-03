namespace ListingStudio.Domain.Properties;

public sealed record PropertyDetails(
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
