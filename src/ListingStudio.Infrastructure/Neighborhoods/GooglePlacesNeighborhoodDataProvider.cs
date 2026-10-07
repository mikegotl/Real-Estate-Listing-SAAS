using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ListingStudio.Application.Neighborhoods;
using ListingStudio.Domain.Neighborhoods;
using ListingStudio.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace ListingStudio.Infrastructure.Neighborhoods;

public sealed class GooglePlacesNeighborhoodDataProvider(
    HttpClient httpClient,
    IOptions<NeighborhoodInsightsOptions> options) : INeighborhoodDataProvider
{
    private readonly NeighborhoodInsightsOptions configuration = options.Value;

    public bool IsConfigured => configuration.Enabled && !string.IsNullOrWhiteSpace(configuration.ApiKey);

    public async Task<IReadOnlyList<NeighborhoodPlaceCandidate>> SearchAsync(
        NeighborhoodSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureConfigured();

        var location = await ResolveLocationAsync(request.FullAddress, cancellationToken)
            ?? throw new InvalidOperationException("The property address could not be located. Verify the address and try again.");

        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/places:searchNearby")
        {
            Content = JsonContent.Create(new
            {
                includedTypes = configuration.PlaceTypes,
                maxResultCount = Math.Clamp(request.MaximumResults, 1, 20),
                rankPreference = "DISTANCE",
                locationRestriction = new
                {
                    circle = new
                    {
                        center = new { latitude = location.Latitude, longitude = location.Longitude },
                        radius = Math.Clamp(request.RadiusMeters, 100, 50_000),
                    },
                },
            }),
        };
        AddHeaders(message, "places.id,places.displayName,places.formattedAddress,places.primaryType,places.location,places.googleMapsUri,places.photos");
        using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, "nearby-place search", cancellationToken);
        var payload = await response.Content.ReadFromJsonAsync<PlacesResponse>(cancellationToken: cancellationToken);

        return payload?.Places?
            .Where(place => !string.IsNullOrWhiteSpace(place.Id)
                && !string.IsNullOrWhiteSpace(place.DisplayName?.Text)
                && place.Location is not null
                && Uri.TryCreate(place.GoogleMapsUri, UriKind.Absolute, out _))
            .Select(place => new NeighborhoodPlaceCandidate(
                place.Id!,
                ToCategory(place.PrimaryType),
                place.DisplayName!.Text!,
                place.FormattedAddress ?? place.DisplayName.Text!,
                CalculateDistanceMiles(location, place.Location!),
                place.GoogleMapsUri!,
                place.Photos?.Count > 0,
                GetPhotoAttribution(place.Photos)?.DisplayName,
                GetPhotoAttribution(place.Photos)?.Uri,
                GetPhotoSourceUrl(place.Photos)))
            .DistinctBy(place => place.ProviderPlaceId, StringComparer.Ordinal)
            .Take(request.MaximumResults)
            .ToArray()
            ?? [];
    }

    public async Task<NeighborhoodPhoto?> OpenPhotoAsync(
        string providerPlaceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerPlaceId);
        EnsureConfigured();

        using var details = new HttpRequestMessage(
            HttpMethod.Get,
            $"v1/places/{Uri.EscapeDataString(providerPlaceId)}?fields=photos");
        AddHeaders(details, null);
        using var detailsResponse = await httpClient.SendAsync(details, cancellationToken);
        await EnsureSuccessAsync(detailsResponse, "place-photo lookup", cancellationToken);
        var place = await detailsResponse.Content.ReadFromJsonAsync<Place>(cancellationToken: cancellationToken);
        var photoName = place?.Photos is { Count: > 0 } photos ? photos[0].Name : null;
        if (string.IsNullOrWhiteSpace(photoName))
        {
            return null;
        }

        using var media = new HttpRequestMessage(
            HttpMethod.Get,
            $"v1/{photoName}/media?maxWidthPx=960&maxHeightPx=640&skipHttpRedirect=false");
        AddHeaders(media, null);
        var mediaResponse = await httpClient.SendAsync(media, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (mediaResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            mediaResponse.Dispose();
            return null;
        }

        await EnsureSuccessAsync(mediaResponse, "place-photo retrieval", cancellationToken);
        var contentType = mediaResponse.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
        var stream = await mediaResponse.Content.ReadAsStreamAsync(cancellationToken);
        return new NeighborhoodPhoto(new ResponseOwnedStream(stream, mediaResponse), contentType);
    }

    private async Task<Location?> ResolveLocationAsync(string address, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/places:searchText")
        {
            Content = JsonContent.Create(new { textQuery = address, maxResultCount = 1 }),
        };
        AddHeaders(message, "places.location");
        using var response = await httpClient.SendAsync(message, cancellationToken);
        await EnsureSuccessAsync(response, "property-address lookup", cancellationToken);
        var payload = await response.Content.ReadFromJsonAsync<PlacesResponse>(cancellationToken: cancellationToken);
        return payload?.Places is { Count: > 0 } places ? places[0].Location : null;
    }

    private void AddHeaders(HttpRequestMessage message, string? fieldMask)
    {
        message.Headers.Add("X-Goog-Api-Key", configuration.ApiKey);
        if (fieldMask is not null)
        {
            message.Headers.Add("X-Goog-FieldMask", fieldMask);
        }
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        var safeDetail = detail.Length > 300 ? detail[..300] : detail;
        throw new HttpRequestException(
            $"Google Places {operation} failed with HTTP {(int)response.StatusCode}: {safeDetail}",
            null,
            response.StatusCode);
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "Neighborhood Insights requires an enabled Google Places API key. Configure NeighborhoodInsights:ApiKey and try again.");
        }
    }

    private static NeighborhoodPlaceCategory ToCategory(string? type) => type switch
    {
        "school" or "primary_school" or "secondary_school" or "preschool" => NeighborhoodPlaceCategory.School,
        "park" => NeighborhoodPlaceCategory.Park,
        "library" => NeighborhoodPlaceCategory.Library,
        "supermarket" or "grocery_store" => NeighborhoodPlaceCategory.Grocery,
        "hospital" or "doctor" or "medical_clinic" or "pharmacy" => NeighborhoodPlaceCategory.Healthcare,
        "restaurant" or "cafe" or "bakery" => NeighborhoodPlaceCategory.Dining,
        "shopping_mall" or "store" => NeighborhoodPlaceCategory.Shopping,
        "bus_station" or "train_station" or "transit_station" => NeighborhoodPlaceCategory.Transit,
        _ => NeighborhoodPlaceCategory.Other,
    };

    private static decimal CalculateDistanceMiles(Location origin, Location destination)
    {
        const double EarthRadiusMiles = 3958.7613;
        var latitudeDelta = DegreesToRadians(destination.Latitude - origin.Latitude);
        var longitudeDelta = DegreesToRadians(destination.Longitude - origin.Longitude);
        var originLatitude = DegreesToRadians(origin.Latitude);
        var destinationLatitude = DegreesToRadians(destination.Latitude);
        var a = Math.Pow(Math.Sin(latitudeDelta / 2), 2)
            + Math.Cos(originLatitude) * Math.Cos(destinationLatitude)
            * Math.Pow(Math.Sin(longitudeDelta / 2), 2);
        var miles = EarthRadiusMiles * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return decimal.Round((decimal)miles, 2, MidpointRounding.AwayFromZero);
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;

    private static AuthorAttribution? GetPhotoAttribution(IReadOnlyList<Photo>? photos)
    {
        if (photos is not { Count: > 0 }
            || photos[0].AuthorAttributions is not { Count: > 0 } attributions)
        {
            return null;
        }

        return attributions[0];
    }

    private static string? GetPhotoSourceUrl(IReadOnlyList<Photo>? photos) =>
        photos is { Count: > 0 } ? photos[0].GoogleMapsUri : null;

    private sealed class PlacesResponse
    {
        public IReadOnlyList<Place>? Places { get; init; }
    }

    private sealed class Place
    {
        public string? Id { get; init; }
        public LocalizedText? DisplayName { get; init; }
        public string? FormattedAddress { get; init; }
        public string? PrimaryType { get; init; }
        public Location? Location { get; init; }
        public string? GoogleMapsUri { get; init; }
        public IReadOnlyList<Photo>? Photos { get; init; }
    }

    private sealed class LocalizedText
    {
        public string? Text { get; init; }
    }

    private sealed class Location
    {
        public double Latitude { get; init; }
        public double Longitude { get; init; }
    }

    private sealed class Photo
    {
        public string? Name { get; init; }
        public string? GoogleMapsUri { get; init; }
        public IReadOnlyList<AuthorAttribution>? AuthorAttributions { get; init; }
    }

    private sealed class AuthorAttribution
    {
        public string? DisplayName { get; init; }
        public string? Uri { get; init; }
    }

    private sealed class ResponseOwnedStream(Stream inner, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            response.Dispose();
            await base.DisposeAsync();
            GC.SuppressFinalize(this);
        }
    }
}
