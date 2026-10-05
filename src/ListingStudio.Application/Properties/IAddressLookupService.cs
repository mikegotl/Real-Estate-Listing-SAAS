namespace ListingStudio.Application.Properties;

public interface IAddressLookupService
{
    Task<IReadOnlyList<AddressLookupSuggestion>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default);

    Task<AddressLookupResult?> ResolveAsync(
        AddressLookupSuggestion suggestion,
        CancellationToken cancellationToken = default);
}

public sealed record AddressLookupSuggestion(string DisplayText, string Reference);

public sealed record AddressLookupResult(
    string Address1,
    string City,
    string State,
    string ZipCode,
    string DisplayText);
