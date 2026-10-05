using System.Net;
using System.Text;
using ListingStudio.Application.Properties;
using ListingStudio.Infrastructure.Configuration;
using ListingStudio.Infrastructure.Properties;
using Microsoft.Extensions.Options;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class ArcGisAddressLookupServiceTests
{
    [Fact]
    public async Task SearchReturnsNonCollectionAddressSuggestions()
    {
        var handler = new RecordingHandler(
            """
            {
              "suggestions": [
                {
                  "text": "1600 Pennsylvania Ave NW, Washington, DC, 20500, USA",
                  "magicKey": "address-key",
                  "isCollection": false
                },
                {
                  "text": "Pennsylvania Avenue",
                  "magicKey": "collection-key",
                  "isCollection": true
                }
              ]
            }
            """);
        using var client = Client(handler);
        var service = Service(client);

        var suggestions = await service.SearchAsync("1600 Pennsylvania Ave NW");

        var suggestion = Assert.Single(suggestions);
        Assert.Equal("1600 Pennsylvania Ave NW, Washington, DC, 20500, USA", suggestion.DisplayText);
        Assert.Equal("address-key", suggestion.Reference);
        Assert.NotNull(handler.LastRequestUri);
        Assert.Equal("/arcgis/rest/services/World/GeocodeServer/suggest", handler.LastRequestUri.AbsolutePath);
        Assert.Contains("countryCode=USA", handler.LastRequestUri.Query, StringComparison.Ordinal);
        Assert.Contains("category=Address", handler.LastRequestUri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveReturnsStructuredAddressFields()
    {
        var handler = new RecordingHandler(
            """
            {
              "candidates": [
                {
                  "address": "1600 Pennsylvania Ave NW, Washington, District of Columbia, 20500",
                  "attributes": {
                    "LongLabel": "1600 Pennsylvania Ave NW, Washington, DC, 20500, USA",
                    "StAddr": "1600 Pennsylvania Ave NW",
                    "City": "Washington",
                    "RegionAbbr": "DC",
                    "Postal": "20500"
                  }
                }
              ]
            }
            """);
        using var client = Client(handler);
        var service = Service(client);

        var result = await service.ResolveAsync(new AddressLookupSuggestion(
            "1600 Pennsylvania Ave NW, Washington, DC, 20500, USA",
            "address-key"));

        Assert.NotNull(result);
        Assert.Equal("1600 Pennsylvania Ave NW", result.Address1);
        Assert.Equal("Washington", result.City);
        Assert.Equal("DC", result.State);
        Assert.Equal("20500", result.ZipCode);
        Assert.NotNull(handler.LastRequestUri);
        Assert.Equal("/arcgis/rest/services/World/GeocodeServer/findAddressCandidates", handler.LastRequestUri.AbsolutePath);
        Assert.Contains("magicKey=address-key", handler.LastRequestUri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisabledLookupMakesNoProviderRequest()
    {
        var handler = new RecordingHandler("{}");
        using var client = Client(handler);
        var service = Service(client, enabled: false);

        var suggestions = await service.SearchAsync("1600 Pennsylvania Ave NW");
        var result = await service.ResolveAsync(new AddressLookupSuggestion("Address", "key"));

        Assert.Empty(suggestions);
        Assert.Null(result);
        Assert.Null(handler.LastRequestUri);
    }

    private static ArcGisAddressLookupService Service(HttpClient client, bool enabled = true) =>
        new(client, Options.Create(new AddressLookupOptions
        {
            Enabled = enabled,
            BaseUrl = client.BaseAddress!.AbsoluteUri,
            CountryCode = "USA",
            MinimumQueryLength = 4,
            MaximumSuggestions = 5,
        }));

    private static HttpClient Client(HttpMessageHandler handler) => new(handler)
    {
        BaseAddress = new Uri("https://geocode.arcgis.test/arcgis/rest/services/World/GeocodeServer/"),
    };

    private sealed class RecordingHandler(string responseJson) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            });
        }
    }
}
