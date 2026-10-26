using System.Net.Http.Headers;
using System.Text.Json;
using ListingStudio.AI.Configuration;
using ListingStudio.Application.Campaigns;
using ListingStudio.Application.Properties;
using Microsoft.Extensions.Options;

namespace ListingStudio.AI.Campaigns;

/// <summary>
/// A restrained image edit for an agent-owned portrait. It intentionally avoids wardrobe, body, age and facial
/// changes; those would make the marketing asset a misleading representation of the agent.
/// </summary>
public sealed class OpenAIAgentPortraitEnhancer(
    HttpClient httpClient,
    IOptions<OpenAIOptions> openAIOptions,
    IOptions<PhotoFixOptions> photoFixOptions) : IAgentPortraitEnhancer
{
    internal const string Prompt = """
        This is a professional real-estate agent portrait supplied and approved by the person shown. Make only
        restrained photographic corrections: neutral white balance, even exposure, natural contrast, mild noise
        reduction, and subtle sharpening. If needed, simplify the existing background into a clean neutral studio
        background without changing the person's outline.

        Preserve the person's identity exactly: facial structure, skin tone, age, hair, eyes, expression, body,
        clothing, jewelry, pose and proportions must remain unchanged. Do not beautify, reshape, de-age, add makeup,
        replace clothing, invent body parts, add text or logos, or create a different person. Keep a realistic,
        professional photograph. Return only the corrected portrait.
        """;

    public bool IsEnabled => photoFixOptions.Value.AgentPortraitEnhancementEnabled
        && !string.IsNullOrWhiteSpace(openAIOptions.Value.ApiKey);

    public async Task<byte[]> EnhanceAsync(
        AgentPortraitEnhancementRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var configuration = photoFixOptions.Value;
        if (!IsEnabled)
        {
            throw new InvalidOperationException("AI headshot fix is not configured.");
        }

        if (!Uri.TryCreate(configuration.ImageEditsEndpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrWhiteSpace(configuration.ImageModel))
        {
            throw new InvalidOperationException(
                "AI headshot fix requires PhotoFixes:ImageModel and an HTTPS PhotoFixes:ImageEditsEndpoint.");
        }

        using var form = new MultipartFormDataContent
        {
            { new StringContent(configuration.ImageModel), "model" },
            { new StringContent(Prompt), "prompt" },
            { new StringContent("1"), "n" },
            { new StringContent("auto"), "size" },
            { new StringContent("png"), "output_format" },
        };
        if (!string.IsNullOrWhiteSpace(configuration.Quality))
        {
            form.Add(new StringContent(configuration.Quality), "quality");
        }

        if (SupportsInputFidelity(configuration.ImageModel)
            && !string.IsNullOrWhiteSpace(configuration.InputFidelity))
        {
            form.Add(new StringContent(configuration.InputFidelity), "input_fidelity");
        }

        var image = new ByteArrayContent(request.Image);
        image.Headers.ContentType = MediaTypeHeaderValue.Parse(request.ContentType);
        form.Add(image, "image", "agent-headshot.jpg");

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
            throw new TimeoutException("The AI headshot fix timed out.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var errorCode = await ErrorCodeAsync(response, timeout.Token);
                var requestId = response.Headers.TryGetValues("x-request-id", out var values)
                    ? values.FirstOrDefault()
                    : null;
                var suffix = requestId is null ? string.Empty : $" (request {requestId})";
                var status = (int)response.StatusCode;
                if (errorCode is "model_not_found" or "invalid_model")
                {
                    throw new PhotoFixRejectedException(
                        $"The configured OpenAI image model is unavailable{suffix}. Please try again after the site is updated.");
                }

                throw status is 400 or 401 or 403 or 404 or 413 or 422
                    ? new PhotoFixRejectedException($"OpenAI declined the headshot edit with HTTP {status}{suffix}.")
                    : new HttpRequestException(
                        $"OpenAI headshot fix failed with HTTP {status}{suffix}.",
                        null,
                        response.StatusCode);
            }

            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token);
            var encoded = json.RootElement.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Array
                && data.GetArrayLength() > 0
                && data[0].TryGetProperty("b64_json", out var output)
                    ? output.GetString()
                    : null;
            if (string.IsNullOrWhiteSpace(encoded))
            {
                throw new InvalidDataException("OpenAI returned no corrected headshot.");
            }

            if (encoded.Length / 4 * 3 > configuration.MaxOutputMegabytes * 1024L * 1024)
            {
                throw new InvalidDataException("The corrected headshot exceeds the configured size limit.");
            }

            return Convert.FromBase64String(encoded);
        }
    }

    private static bool SupportsInputFidelity(string model) =>
        !model.StartsWith("gpt-image-2", StringComparison.OrdinalIgnoreCase);

    private static async Task<string?> ErrorCodeAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
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
            return null;
        }
    }
}
