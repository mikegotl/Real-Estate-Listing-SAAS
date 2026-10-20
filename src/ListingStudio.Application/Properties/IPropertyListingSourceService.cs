using System.Text.RegularExpressions;
using ListingStudio.Domain.Properties;

namespace ListingStudio.Application.Properties;

public interface IPropertyListingSourceService
{
    Task<PropertyListingSourceItem?> GetAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Links a canonical Zillow home-details page after the user confirms that it represents this property.
    /// This does not scrape or copy Zillow content.
    /// </summary>
    Task<PropertyListingSourceItem?> LinkZillowAsync(
        string userId,
        Guid propertyId,
        string sourceUrl,
        bool addressConfirmed,
        CancellationToken cancellationToken = default);

    Task<bool> RemoveAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default);
}

public sealed record PropertyListingSourceItem(
    Guid Id,
    PropertyListingProvider Provider,
    string SourceUrl,
    string ExternalListingId,
    DateTimeOffset AddressConfirmedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record ZillowListingLink(string Url, string ZillowPropertyId)
{
    private static readonly Regex ZillowPropertyIdPattern = new(
        @"(?<id>[0-9]+)_zpid(?:/|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static ZillowListingLink Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 2_000
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || uri.Port != 443
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !(string.Equals(uri.Host, "zillow.com", StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Host, "www.zillow.com", StringComparison.OrdinalIgnoreCase))
            || !uri.AbsolutePath.StartsWith("/homedetails/", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Enter a secure Zillow home-details link, such as https://www.zillow.com/homedetails/.../123456_zpid/.",
                nameof(value));
        }

        var match = ZillowPropertyIdPattern.Match(uri.AbsolutePath);
        if (!match.Success)
        {
            throw new ArgumentException(
                "This Zillow link does not contain a property ID. Open the home's details page and copy its full link.",
                nameof(value));
        }

        var normalized = new UriBuilder(Uri.UriSchemeHttps, "www.zillow.com")
        {
            Path = uri.AbsolutePath,
            Query = string.Empty,
            Fragment = string.Empty,
            Port = -1,
        }.Uri.AbsoluteUri;

        return new ZillowListingLink(normalized, match.Groups["id"].Value);
    }

    public bool AppearsToMatch(string address1, string zipCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address1);
        ArgumentException.ThrowIfNullOrWhiteSpace(zipCode);
        var pathTokens = Tokens(Uri.UnescapeDataString(new Uri(Url).AbsolutePath));
        var addressTokens = Tokens(address1);
        var houseNumber = addressTokens.FirstOrDefault(token => token.Any(char.IsDigit));
        var streetName = addressTokens.FirstOrDefault(token => token != houseNumber
            && token.Length >= 3
            && !IgnoredAddressTokens.Contains(token));
        var zip = new string(zipCode.Where(char.IsDigit).Take(5).ToArray());
        return houseNumber is not null
            && streetName is not null
            && zip.Length == 5
            && pathTokens.Contains(houseNumber)
            && pathTokens.Contains(streetName)
            && pathTokens.Contains(zip);
    }

    private static readonly HashSet<string> IgnoredAddressTokens = new(StringComparer.Ordinal)
    {
        "north", "south", "east", "west", "northeast", "northwest", "southeast", "southwest",
        "street", "avenue", "boulevard", "circle", "court", "drive", "highway", "lane", "parkway",
        "place", "road", "square", "terrace", "trail", "way", "ave", "blvd", "cir", "ct", "dr",
        "hwy", "pkwy", "pl", "sq", "ter",
    };

    private static string[] Tokens(string value) => Regex
        .Matches(value.ToLowerInvariant(), "[a-z0-9]+", RegexOptions.CultureInvariant)
        .Select(match => match.Value)
        .ToArray();
}
