using System.Globalization;

namespace ListingStudio.Application.Leads;

/// <summary>
/// The page title and description search engines and link previews show for a public listing page.
/// </summary>
public static class PublicListingSearchText
{
    public const int MaximumDescriptionLength = 160;

    private static readonly CultureInfo UnitedStates = CultureInfo.GetCultureInfo("en-US");

    /// <summary>For example "123 Oak St, Austin, TX · 3 bd · 2 ba · $485,000".</summary>
    public static string Title(PublicListingResult listing)
    {
        ArgumentNullException.ThrowIfNull(listing);
        return string.Join(" · ",
            $"{listing.Address1}, {listing.City}, {listing.State}",
            $"{listing.Bedrooms} bd",
            $"{listing.Bathrooms.ToString("0.#", UnitedStates)} ba",
            listing.ListingPrice.ToString("C0", UnitedStates));
    }

    /// <summary>
    /// The listing's own description, cut at a word boundary, or a factual summary when the agent wrote none.
    /// </summary>
    public static string Description(PublicListingResult listing)
    {
        ArgumentNullException.ThrowIfNull(listing);
        var text = string.IsNullOrWhiteSpace(listing.Description)
            ? Summary(listing)
            : string.Join(' ', listing.Description.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length <= MaximumDescriptionLength)
        {
            return text;
        }

        var cut = text[..(MaximumDescriptionLength - 1)];
        var lastSpace = cut.LastIndexOf(' ');
        if (lastSpace > MaximumDescriptionLength / 2)
        {
            cut = cut[..lastSpace];
        }

        return cut.TrimEnd(' ', ',', ';', ':', '.', '-') + "…";
    }

    private static string Summary(PublicListingResult listing)
    {
        var size = listing.SquareFeet is { } squareFeet
            ? $", {squareFeet.ToString("N0", UnitedStates)} sq ft"
            : string.Empty;
        return $"{listing.Bedrooms} bed, {listing.Bathrooms.ToString("0.#", UnitedStates)} bath home{size} "
            + $"in {listing.City}, {listing.State}, listed at {listing.ListingPrice.ToString("C0", UnitedStates)}. "
            + (listing.HasVideo ? "Watch the tour and contact the listing agent." : "Contact the listing agent.");
    }
}
