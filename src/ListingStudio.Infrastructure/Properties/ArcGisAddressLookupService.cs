using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ListingStudio.Application.Properties;
using ListingStudio.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace ListingStudio.Infrastructure.Properties;

public sealed class ArcGisAddressLookupService(
    HttpClient httpClient,
    IOptions<AddressLookupOptions> options) : IAddressLookupService
{
    private readonly AddressLookupOptions _options = options.Value;

    public async Task<IReadOnlyList<AddressLookupSuggestion>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        var normalized = query.Trim();
        if (!_options.Enabled || normalized.Length < _options.MinimumQueryLength)
        {
            return [];
        }

        var requestUri = "suggest"
            + $"?text={Uri.EscapeDataString(normalized)}"
            + $"&countryCode={Uri.EscapeDataString(_options.CountryCode)}"
            + "&category=Address"
            + $"&maxSuggestions={_options.MaximumSuggestions}"
            + "&f=json";

        var response = await httpClient.GetFromJsonAsync<SuggestionResponse>(requestUri, cancellationToken);
        return response?.Suggestions?
            .Where(suggestion => !suggestion.IsCollection
                && !string.IsNullOrWhiteSpace(suggestion.Text)
                && !string.IsNullOrWhiteSpace(suggestion.MagicKey))
            .Select(suggestion => new AddressLookupSuggestion(suggestion.Text!, suggestion.MagicKey!))
            .DistinctBy(suggestion => suggestion.DisplayText, StringComparer.OrdinalIgnoreCase)
            .Take(_options.MaximumSuggestions)
            .ToArray()
            ?? [];
    }

    public async Task<AddressLookupResult?> ResolveAsync(
        AddressLookupSuggestion suggestion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestion.DisplayText);
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestion.Reference);

        if (!_options.Enabled)
        {
            return null;
        }

        var requestUri = "findAddressCandidates"
            + $"?SingleLine={Uri.EscapeDataString(suggestion.DisplayText)}"
            + $"&magicKey={Uri.EscapeDataString(suggestion.Reference)}"
            + $"&countryCode={Uri.EscapeDataString(_options.CountryCode)}"
            + "&category=Address"
            + "&maxLocations=1"
            + "&outFields=Match_addr,LongLabel,ShortLabel,Addr_type,StAddr,City,RegionAbbr,Postal,Country"
            + "&f=json";

        var response = await httpClient.GetFromJsonAsync<CandidateResponse>(requestUri, cancellationToken);
        var candidate = response?.Candidates is { Count: > 0 } candidates ? candidates[0] : null;
        var attributes = candidate?.Attributes;
        if (attributes is null
            || string.IsNullOrWhiteSpace(attributes.StreetAddress)
            || string.IsNullOrWhiteSpace(attributes.City)
            || string.IsNullOrWhiteSpace(attributes.RegionAbbreviation)
            || string.IsNullOrWhiteSpace(attributes.PostalCode))
        {
            return null;
        }

        return new AddressLookupResult(
            attributes.StreetAddress,
            attributes.City,
            attributes.RegionAbbreviation,
            attributes.PostalCode,
            attributes.LongLabel ?? candidate?.Address ?? suggestion.DisplayText);
    }

    private sealed class SuggestionResponse
    {
        public IReadOnlyList<Suggestion>? Suggestions { get; init; }
    }

    private sealed class Suggestion
    {
        public string? Text { get; init; }

        public string? MagicKey { get; init; }

        public bool IsCollection { get; init; }
    }

    private sealed class CandidateResponse
    {
        public IReadOnlyList<Candidate>? Candidates { get; init; }
    }

    private sealed class Candidate
    {
        public string? Address { get; init; }

        public CandidateAttributes? Attributes { get; init; }
    }

    private sealed class CandidateAttributes
    {
        [JsonPropertyName("LongLabel")]
        public string? LongLabel { get; init; }

        [JsonPropertyName("StAddr")]
        public string? StreetAddress { get; init; }

        public string? City { get; init; }

        [JsonPropertyName("RegionAbbr")]
        public string? RegionAbbreviation { get; init; }

        [JsonPropertyName("Postal")]
        public string? PostalCode { get; init; }
    }
}
