using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Videos;
using ListingStudio.Video.Configuration;
using Microsoft.Extensions.Options;

namespace ListingStudio.Video.Generation;

public sealed class HttpAiVideoProvider(
    HttpClient httpClient,
    IOptions<AIVideoOptions> options) : IAiVideoProvider
{
    private static readonly HashSet<string> AllowedMotionInstructions = new(StringComparer.OrdinalIgnoreCase)
    {
        "slow cinematic push forward",
        "slow cinematic pull back",
        "slow horizontal pan",
        "slow camera push while preserving the property image",
    };

    private const string PromptTemplate = """
        Create subtle cinematic camera movement from this real-estate listing photograph.

        Preserve the architecture, furniture, windows, doors, fixtures, landscaping and spatial layout exactly as shown.
        Do not add, remove or redesign property features.

        Movement:
        {0}

        Maintain realistic real-estate photography.
        No people.
        No text.
        No architectural hallucinations.
        """;

    public bool IsEnabled => options.Value.Enabled;

    public string GenerationVersion
    {
        get
        {
            var configuration = options.Value;
            var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{configuration.Provider}\n{configuration.Model}")))[..12].ToLowerInvariant();
            return $"http-ai-video-v1/{configuration.GenerationVersion}/{identity}";
        }
    }

    public async Task<AiVideoProviderResult> GenerateAsync(
        AiVideoProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var configuration = options.Value;
        Validate(configuration);
        if (!IsEnabled)
        {
            throw new InvalidOperationException("AI video generation is disabled.");
        }

        if (request.IdempotencyKey.Length != 64 || request.IdempotencyKey.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("The AI video idempotency key must be a SHA-256 hexadecimal value.", nameof(request));
        }

        if (!request.Content.CanRead
            || string.IsNullOrWhiteSpace(request.OriginalFilename)
            || request.ContentType is not ("image/jpeg" or "image/png" or "image/webp")
            || !AllowedMotionInstructions.Contains(request.MotionInstruction)
            || request.DurationMs is < 1_000 or > 60_000
            || !Enum.IsDefined(request.AspectRatio))
        {
            throw new ArgumentException("The AI video request is outside the supported input contract.", nameof(request));
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(configuration.RequestTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var form = new MultipartFormDataContent();
        using var image = new StreamContent(request.Content);
        image.Headers.ContentType = MediaTypeHeaderValue.Parse(request.ContentType);
        form.Add(image, "image", request.OriginalFilename);
        form.Add(new StringContent(PromptTemplate.Replace(
            "{0}",
            request.MotionInstruction,
            StringComparison.Ordinal)), "prompt");
        form.Add(new StringContent((request.DurationMs / 1_000m).ToString(CultureInfo.InvariantCulture)), "duration_seconds");
        form.Add(new StringContent(request.AspectRatio == VideoAspectRatio.Landscape16By9 ? "16:9" : "9:16"), "aspect_ratio");
        form.Add(new StringContent(configuration.Model), "model");

        using var message = new HttpRequestMessage(HttpMethod.Post, configuration.Endpoint) { Content = form };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration.ApiKey);
        message.Headers.Add("Idempotency-Key", request.IdempotencyKey);
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                linked.Token);
        }
        catch (OperationCanceledException exception) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The AI video provider request timed out.", exception);
        }

        using var responseDisposable = response;
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"AI video generation failed with HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }

        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(contentType, "video/mp4", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The AI video provider did not return an MP4 video.");
        }

        var width = RequiredIntHeader(response, "X-Video-Width");
        var height = RequiredIntHeader(response, "X-Video-Height");
        var durationMs = RequiredIntHeader(response, "X-Video-Duration-Ms");
        var requestId = OptionalHeader(response, "X-Provider-Request-Id");
        var model = OptionalHeader(response, "X-Provider-Model") ?? configuration.Model;
        var estimatedCost = DecimalHeader(response, "X-Estimated-Cost-Usd");
        await using var source = await response.Content.ReadAsStreamAsync(linked.Token);
        var maximumBytes = checked(configuration.MaxOutputMegabytes * 1024L * 1024L);
        var output = new MemoryStream();
        await CopyBoundedAsync(source, output, maximumBytes, linked.Token);
        output.Position = 0;
        return new AiVideoProviderResult(
            output,
            "video/mp4",
            configuration.Provider,
            model,
            requestId,
            durationMs,
            width,
            height,
            estimatedCost,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["transport"] = "synchronous-multipart-v1",
            });
    }

    private static void Validate(AIVideoOptions options)
    {
        if (!options.Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(options.Provider)
            || string.IsNullOrWhiteSpace(options.ApiKey)
            || string.IsNullOrWhiteSpace(options.Model)
            || string.IsNullOrWhiteSpace(options.GenerationVersion)
            || options.GenerationVersion.Length > 64
            || !Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                "Enabled AI video requires Provider, ApiKey, Model, GenerationVersion, and an HTTPS Endpoint.");
        }

        if (options.RequestTimeoutSeconds is < 30 or > 3_600
            || options.MaxOutputMegabytes is < 1 or > 500)
        {
            throw new InvalidOperationException("AI video timeout or output-size configuration is invalid.");
        }
    }

    private static string? OptionalHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.SingleOrDefault() : null;

    private static int RequiredIntHeader(HttpResponseMessage response, string name) =>
        int.TryParse(OptionalHeader(response, name), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            && value > 0
                ? value
                : throw new InvalidDataException($"The AI video response is missing a valid {name} header.");

    private static decimal? DecimalHeader(HttpResponseMessage response, string name)
    {
        var value = OptionalHeader(response, name);
        if (value is null)
        {
            return null;
        }

        return decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed)
            && parsed >= 0
                ? parsed
                : throw new InvalidDataException($"The AI video response has an invalid {name} header.");
    }

    private static async Task CopyBoundedAsync(
        Stream source,
        Stream destination,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81_920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > maximumBytes)
            {
                throw new InvalidDataException("The AI video response exceeds the configured size limit.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        if (total == 0)
        {
            throw new InvalidDataException("The AI video provider returned an empty video.");
        }
    }
}
