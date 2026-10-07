using System.Net;
using System.Text;
using ListingStudio.Application.Neighborhoods;
using ListingStudio.Domain.Neighborhoods;
using ListingStudio.Infrastructure.Configuration;
using ListingStudio.Infrastructure.Neighborhoods;
using Microsoft.Extensions.Options;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class GooglePlacesNeighborhoodDataProviderTests
{
    [Fact]
    public async Task SearchMapsNearbyPlacesDistancesAndAttribution()
    {
        var handler = new SequentialHandler(
            """
            { "places": [ { "location": { "latitude": 28.641, "longitude": -81.124 } } ] }
            """,
            """
            {
              "places": [
                {
                  "id": "school-place-id",
                  "displayName": { "text": "Example Elementary School" },
                  "formattedAddress": "10 Learning Lane, Chuluota, FL 32766",
                  "primaryType": "primary_school",
                  "location": { "latitude": 28.651, "longitude": -81.124 },
                  "googleMapsUri": "https://maps.google.com/?cid=123",
                  "photos": [
                    {
                      "name": "places/school/photos/photo-1",
                      "googleMapsUri": "https://maps.google.com/photo/123",
                      "authorAttributions": [ { "displayName": "Alex Example", "uri": "https://maps.google.com/user/456" } ]
                    }
                  ]
                }
              ]
            }
            """);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://places.googleapis.test/") };
        var provider = new GooglePlacesNeighborhoodDataProvider(client, Options.Create(new NeighborhoodInsightsOptions
        {
            Enabled = true,
            ApiKey = "test-key",
            BaseUrl = client.BaseAddress.AbsoluteUri,
            PlaceTypes = ["school"],
        }));

        var results = await provider.SearchAsync(new NeighborhoodSearchRequest(
            "121 E 7th St, Chuluota, FL 32766", 8_000, 12));

        var result = Assert.Single(results);
        Assert.Equal(NeighborhoodPlaceCategory.School, result.Category);
        Assert.Equal("Example Elementary School", result.Name);
        Assert.InRange(result.DistanceMiles, 0.68m, 0.70m);
        Assert.True(result.HasPhoto);
        Assert.Equal("Alex Example", result.PhotoAttribution);
        Assert.Equal("https://maps.google.com/user/456", result.PhotoAttributionUrl);
        Assert.Equal("https://maps.google.com/photo/123", result.PhotoSourceUrl);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal("test-key", request.ApiKey));
        Assert.Contains("places.location", handler.Requests[0].FieldMask, StringComparison.Ordinal);
        Assert.Contains("places.photos", handler.Requests[1].FieldMask, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisabledProviderMakesNoRequest()
    {
        var handler = new SequentialHandler("{}");
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://places.googleapis.test/") };
        var provider = new GooglePlacesNeighborhoodDataProvider(client, Options.Create(new NeighborhoodInsightsOptions()));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SearchAsync(
            new NeighborhoodSearchRequest("123 Main Street", 8_000, 12)));

        Assert.Contains("requires an enabled Google Places API key", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    private sealed class SequentialHandler(params string[] responses) : HttpMessageHandler
    {
        private int index;
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.RequestUri!,
                request.Headers.TryGetValues("X-Goog-Api-Key", out var keys) ? keys.Single() : null,
                request.Headers.TryGetValues("X-Goog-FieldMask", out var masks) ? masks.Single() : string.Empty,
                body));
            var response = responses[Math.Min(index, responses.Length - 1)];
            index++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed record CapturedRequest(Uri Uri, string? ApiKey, string FieldMask, string? Body);
}
