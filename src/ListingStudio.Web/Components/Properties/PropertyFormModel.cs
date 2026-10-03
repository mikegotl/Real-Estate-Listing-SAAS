using System.ComponentModel.DataAnnotations;
using ListingStudio.Application.Properties;
using ListingStudio.Domain.Properties;

namespace ListingStudio.Web.Components.Properties;

public sealed class PropertyFormModel
{
    [Required, StringLength(200)]
    public string Address1 { get; set; } = string.Empty;

    [StringLength(200)]
    public string? Address2 { get; set; }

    [Required, StringLength(100)]
    public string City { get; set; } = string.Empty;

    [Required, StringLength(100)]
    public string State { get; set; } = string.Empty;

    [Required, StringLength(20)]
    public string ZipCode { get; set; } = string.Empty;

    [Range(typeof(decimal), "0", "9999999999999999")]
    public decimal ListingPrice { get; set; }

    [Range(0, 100)]
    public int Bedrooms { get; set; }

    [Range(typeof(decimal), "0", "99.9")]
    public decimal Bathrooms { get; set; }

    [Range(1, int.MaxValue)]
    public int? SquareFeet { get; set; }

    [Range(typeof(decimal), "0.01", "9999999999.99")]
    public decimal? LotSize { get; set; }

    [Range(1600, 2200)]
    public int? YearBuilt { get; set; }

    [Required]
    public PropertyType PropertyType { get; set; } = PropertyType.SingleFamily;

    [StringLength(4_000)]
    public string? Description { get; set; }

    [Required]
    public ListingStatus ListingStatus { get; set; } = ListingStatus.Draft;

    public PropertyInput ToInput() => new(
        Address1,
        Address2,
        City,
        State,
        ZipCode,
        ListingPrice,
        Bedrooms,
        Bathrooms,
        SquareFeet,
        LotSize,
        YearBuilt,
        PropertyType,
        Description,
        ListingStatus);

    public static PropertyFormModel From(PropertyDetailsResult property) => new()
    {
        Address1 = property.Address1,
        Address2 = property.Address2,
        City = property.City,
        State = property.State,
        ZipCode = property.ZipCode,
        ListingPrice = property.ListingPrice,
        Bedrooms = property.Bedrooms,
        Bathrooms = property.Bathrooms,
        SquareFeet = property.SquareFeet,
        LotSize = property.LotSize,
        YearBuilt = property.YearBuilt,
        PropertyType = property.PropertyType,
        Description = property.Description,
        ListingStatus = property.ListingStatus,
    };
}
