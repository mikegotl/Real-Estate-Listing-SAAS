using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using ListingStudio.AI.Configuration;
using ListingStudio.Application.Properties;
using Microsoft.Extensions.Options;

namespace ListingStudio.AI.Properties;

/// <summary>
/// Removes clutter with OpenAI's image edit endpoint. The prompt limits the edit to movable items, because a listing
/// photo must not misrepresent the home: walls, floors, fixtures, finishes, views and condition stay as they are.
/// </summary>
public sealed class OpenAIPhotoClutterRemover(
    HttpClient httpClient,
    IOptions<OpenAIOptions> openAIOptions,
    IOptions<PhotoFixOptions> photoFixOptions) : IPhotoClutterRemover
{
    public const string Prompt = """
        This is a real estate listing photo. Remove only temporary, movable clutter so the space looks tidy, and
        change nothing else.

        Remove things like: loose personal items, toiletries, dishes, food, small appliances and bottles left on
        counters, papers, mail, magnets and notes on the refrigerator, toys, pet bowls and pet beds, shoes, coats and
        laundry, bags and boxes, trash and recycling bins, loose cords and chargers, cleaning supplies, and garden
        hoses or tools left out.

        Keep exactly as they are: the walls, floors, ceilings, windows, doors, stairs, railings, trim, cabinets,
        countertops, built-in appliances, plumbing and light fixtures, fireplaces, built-in shelving, the furniture,
        rugs, artwork and decor that furnish the room, the landscaping, the building, and everything seen through
        the windows. Keep the room's size, layout and condition. Do not repair, renovate, restyle or improve
        anything, do not change any color, material or finish, and do not add any object.

        Fill the place where an item was removed with the surface that was behind it, matching its texture and
        lighting. Keep the camera position, framing, lens and lighting identical. If there is no clutter, return
        the photo unchanged. The plain gray bands at the edges are padding: leave them plain gray.
        """;

    public bool IsEnabled => photoFixOptions.Value.RemoveClutterEnabled
        && !string.IsNullOrWhiteSpace(openAIOptions.Value.ApiKey);

    public async Task<byte[]> RemoveClutterAsync(
        PhotoClutterRemovalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var configuration = photoFixOptions.Value;
        if (!IsEnabled)
        {
            throw new InvalidOperationException("Remove clutter is not configured.");
        }

        if (!Uri.TryCreate(configuration.ImageEditsEndpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrWhiteSpace(configuration.ImageModel))
        {
            throw new InvalidOperationException(
                "Remove clutter requires PhotoFixes:ImageModel and an HTTPS PhotoFixes:ImageEditsEndpoint.");
        }

        using var form = new MultipartFormDataContent
        {
            { new StringContent(configuration.ImageModel), "model" },
            { new StringContent(Prompt), "prompt" },
            { new StringContent("1"), "n" },
            { new StringContent(Size(request)), "size" },
            { new StringContent("png"), "output_format" },
        };
        if (!string.IsNullOrWhiteSpace(configuration.Quality))
        {
            form.Add(new StringContent(configuration.Quality), "quality");
        }

        if (!string.IsNullOrWhiteSpace(configuration.InputFidelity))
        {
            form.Add(new StringContent(configuration.InputFidelity), "input_fidelity");
        }

        form.Add(Png(request.Canvas), "image", "photo.png");
        form.Add(Png(request.Mask), "mask", "mask.png");

        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = form };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", openAIOptions.Value.ApiKey);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(configuration.RequestTimeoutSeconds));
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The clutter removal request timed out.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw Failure(response, await ErrorCodeAsync(response, timeout.Token));
            }

            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token);

            var encoded = json.RootElement.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Array
                && data.GetArrayLength() > 0
                && data[0].TryGetProperty("b64_json", out var image)
                    ? image.GetString()
                    : null;
            if (string.IsNullOrWhiteSpace(encoded))
            {
                throw new InvalidDataException("OpenAI returned no edited photo.");
            }

            if (encoded.Length / 4 * 3 > configuration.MaxOutputMegabytes * 1024L * 1024)
            {
                throw new InvalidDataException("The edited photo exceeds the configured size limit.");
            }

            return Convert.FromBase64String(encoded);
        }
    }

    private static string Size(PhotoClutterRemovalRequest request) =>
        $"{request.Width.ToString(CultureInfo.InvariantCulture)}x{request.Height.ToString(CultureInfo.InvariantCulture)}";

    private static ByteArrayContent Png(byte[] content)
    {
        var part = new ByteArrayContent(content);
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return part;
    }

    // A moderation block or a rejected request will fail the same way again, so it is not retried; the request id
    // is kept for support, and OpenAI's own message is left out because it can quote the request.
    private static Exception Failure(HttpResponseMessage response, string? code)
    {
        var requestId = response.Headers.TryGetValues("x-request-id", out var values) ? values.FirstOrDefault() : null;
        var suffix = requestId is null ? string.Empty : $" (request {requestId})";
        if (code == "moderation_blocked")
        {
            return new PhotoFixRejectedException($"OpenAI declined to edit this photo{suffix}.");
        }

        var status = (int)response.StatusCode;
        return status is 400 or 401 or 403 or 404 or 413 or 422
            ? new PhotoFixRejectedException($"OpenAI rejected the clutter removal request with HTTP {status}{suffix}.")
            : new HttpRequestException(
                $"OpenAI clutter removal failed with HTTP {status}{suffix}.",
                null,
                response.StatusCode);
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.String
                    ? code.GetString()
                    : null;
        }
        catch (JsonException)
        {
            // A gateway error page instead of OpenAI's JSON error; the HTTP status says enough.
            return null;
        }
    }
}
