using System.Globalization;
using System.Text.Json;
using ListingStudio.Application.Leads;

namespace ListingStudio.Web.Seo;

/// <summary>schema.org JSON-LD for the public pages.</summary>
public static class SeoStructuredData
{
    // The default encoder escapes <, > and &, so the output cannot close the surrounding script element.
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static string Serialize(object value) => JsonSerializer.Serialize(value, Options);

    public static object Site(Uri baseUri, string description)
    {
        var home = baseUri.AbsoluteUri;
        return new Dictionary<string, object>
        {
            ["@context"] = "https://schema.org",
            ["@graph"] = new object[]
            {
                new Dictionary<string, object>
                {
                    ["@type"] = "Organization",
                    ["@id"] = home + "#organization",
                    ["name"] = "Creative Listing Studio",
                    ["url"] = home,
                    ["logo"] = new Uri(baseUri, "icon-512.png").AbsoluteUri,
                },
                new Dictionary<string, object>
                {
                    ["@type"] = "WebSite",
                    ["@id"] = home + "#website",
                    ["name"] = "Creative Listing Studio",
                    ["url"] = home,
                    ["publisher"] = new Dictionary<string, object> { ["@id"] = home + "#organization" },
                },
                new Dictionary<string, object>
                {
                    ["@type"] = "SoftwareApplication",
                    ["name"] = "Creative Listing Studio",
                    ["url"] = home,
                    ["applicationCategory"] = "BusinessApplication",
                    ["operatingSystem"] = "Web",
                    ["description"] = description,
                    ["publisher"] = new Dictionary<string, object> { ["@id"] = home + "#organization" },
                },
            },
        };
    }

    public static object Listing(PublicListingResult listing, string pageUrl, string? imageUrl, string? videoUrl)
    {
        var description = PublicListingSearchText.Description(listing);
        var residence = new Dictionary<string, object>
        {
            ["@type"] = ResidenceType(listing.PropertyType),
            ["name"] = $"{listing.Address1}, {listing.City}, {listing.State}",
            ["address"] = new Dictionary<string, object>
            {
                ["@type"] = "PostalAddress",
                ["streetAddress"] = string.IsNullOrWhiteSpace(listing.Address2)
                    ? listing.Address1
                    : $"{listing.Address1} {listing.Address2}",
                ["addressLocality"] = listing.City,
                ["addressRegion"] = listing.State,
                ["postalCode"] = listing.ZipCode,
                ["addressCountry"] = "US",
            },
            ["numberOfBedrooms"] = listing.Bedrooms,
            ["numberOfBathroomsTotal"] = listing.Bathrooms,
        };
        if (listing.SquareFeet is { } squareFeet)
        {
            residence["floorSize"] = new Dictionary<string, object>
            {
                ["@type"] = "QuantitativeValue",
                ["value"] = squareFeet,
                ["unitCode"] = "FTK",
            };
        }

        if (listing.YearBuilt is { } yearBuilt)
        {
            residence["yearBuilt"] = yearBuilt;
        }

        var page = new Dictionary<string, object>
        {
            ["@context"] = "https://schema.org",
            ["@type"] = "RealEstateListing",
            ["name"] = listing.Headline ?? $"{listing.Address1}, {listing.City}, {listing.State}",
            ["description"] = description,
            ["url"] = pageUrl,
            ["about"] = residence,
            ["offers"] = new Dictionary<string, object>
            {
                ["@type"] = "Offer",
                ["price"] = listing.ListingPrice.ToString("0.##", CultureInfo.InvariantCulture),
                ["priceCurrency"] = "USD",
                ["seller"] = new Dictionary<string, object>
                {
                    ["@type"] = "RealEstateAgent",
                    ["name"] = listing.OrganizationName,
                },
            },
        };
        if (imageUrl is not null)
        {
            page["image"] = imageUrl;
        }

        if (videoUrl is not null)
        {
            var video = new Dictionary<string, object>
            {
                ["@type"] = "VideoObject",
                ["name"] = $"Tour of {listing.Address1}, {listing.City}",
                ["description"] = description,
                ["contentUrl"] = videoUrl,
            };
            if (imageUrl is not null)
            {
                video["thumbnailUrl"] = imageUrl;
            }

            page["video"] = video;
        }

        return page;
    }

    private static string ResidenceType(string propertyType) => propertyType switch
    {
        "SingleFamily" => "SingleFamilyResidence",
        "Condominium" => "Apartment",
        "Townhouse" => "House",
        "MultiFamily" => "Residence",
        "Land" => "Place",
        _ => "Accommodation",
    };
}
