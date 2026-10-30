using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ListingStudio.Application.Videos;
using ListingStudio.Video.Configuration;
using Microsoft.Extensions.Options;

namespace ListingStudio.Video.Generation;

public sealed class HeyGenAgentAvatarVideoProvider(
    HttpClient httpClient,
    IOptions<HeyGenOptions> options) : IAgentAvatarVideoProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public bool IsEnabled => options.Value.Enabled;

    public int MaximumIntroDurationMs => checked(options.Value.IntroMaximumSeconds * 1_000);

    public int MaximumOutroDurationMs => checked(options.Value.OutroMaximumSeconds * 1_000);

    public string GenerationVersion
    {
        get
        {
            var configuration = options.Value;
            var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{configuration.BaseUrl}\n{configuration.Model}\n{configuration.Resolution}")))[..12]
                .ToLowerInvariant();
            return $"heygen-v3/{configuration.GenerationVersion}/{identity}";
        }
    }

    public async Task<AgentAvatarVideoProviderResult> GenerateAsync(
        AgentAvatarVideoProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var configuration = options.Value;
        Validate(configuration);
        Validate(request);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(configuration.RequestTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var portraitAssetId = await UploadAssetAsync(
                request.Portrait,
                request.PortraitFilename,
                request.PortraitContentType,
                $"{request.IdempotencyKey}:portrait",
                configuration,
                linked.Token);
            var audioAssetId = await UploadAssetAsync(
                request.Audio,
                request.AudioFilename,
                request.AudioContentType,
                $"{request.IdempotencyKey}:audio",
                configuration,
                linked.Token);
            var videoId = await CreateVideoAsync(
                portraitAssetId,
                audioAssetId,
                request.IdempotencyKey,
                configuration,
                linked.Token);
            var completed = await WaitForVideoAsync(videoId, configuration, linked.Token);
            var content = await DownloadAsync(completed.VideoUrl!, configuration.MaxOutputMegabytes, linked.Token);
            var estimatedCost = Math.Round(
                request.DurationMs / 60_000m * configuration.EstimatedCostPerMinuteUsd,
                6,
                MidpointRounding.AwayFromZero);
            return new AgentAvatarVideoProviderResult(
                content,
                "video/mp4",
                "HeyGen",
                configuration.Model,
                videoId,
                checked((int)Math.Round(completed.Duration!.Value * 1_000m)),
                720,
                720,
                estimatedCost,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["transport"] = "heygen-v3-image-audio",
                    ["resolution"] = configuration.Resolution,
                });
        }
        catch (OperationCanceledException exception) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("HeyGen agent presenter generation timed out.", exception);
        }
    }

    private async Task<string> UploadAssetAsync(
        Stream content,
        string filename,
        string contentType,
        string idempotencyKey,
        HeyGenOptions configuration,
        CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();
        using var file = new StreamContent(content);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        form.Add(file, "file", filename);
        using var message = Request(HttpMethod.Post, configuration, "/v3/assets", idempotencyKey);
        message.Content = form;
        using var response = await httpClient.SendAsync(message, cancellationToken);
        await EnsureSuccessAsync(response, "HeyGen asset upload", cancellationToken);
        var envelope = await response.Content.ReadFromJsonAsync<Envelope<UploadAssetResponse>>(
            JsonOptions,
            cancellationToken);
        return !string.IsNullOrWhiteSpace(envelope?.Data?.AssetId)
            ? envelope.Data.AssetId
            : throw new InvalidDataException("HeyGen asset upload returned no asset ID.");
    }

    private async Task<string> CreateVideoAsync(
        string portraitAssetId,
        string audioAssetId,
        string idempotencyKey,
        HeyGenOptions configuration,
        CancellationToken cancellationToken)
    {
        using var message = Request(HttpMethod.Post, configuration, "/v3/videos", idempotencyKey);
        message.Content = JsonContent.Create(new
        {
            type = "image",
            image = new { type = "asset_id", asset_id = portraitAssetId },
            audio_asset_id = audioAssetId,
            title = "Creative Listing Studio agent presenter",
            aspect_ratio = "1:1",
            resolution = configuration.Resolution,
            fit = "cover",
            expressiveness = "low",
        });
        using var response = await httpClient.SendAsync(message, cancellationToken);
        await EnsureSuccessAsync(response, "HeyGen presenter creation", cancellationToken);
        var envelope = await response.Content.ReadFromJsonAsync<Envelope<CreateVideoResponse>>(
            JsonOptions,
            cancellationToken);
        return !string.IsNullOrWhiteSpace(envelope?.Data?.VideoId)
            ? envelope.Data.VideoId
            : throw new InvalidDataException("HeyGen presenter creation returned no video ID.");
    }

    private async Task<VideoStatusResponse> WaitForVideoAsync(
        string videoId,
        HeyGenOptions configuration,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            using var message = Request(
                HttpMethod.Get,
                configuration,
                $"/v3/videos/{Uri.EscapeDataString(videoId)}");
            using var response = await httpClient.SendAsync(message, cancellationToken);
            await EnsureSuccessAsync(response, "HeyGen presenter status", cancellationToken);
            var envelope = await response.Content.ReadFromJsonAsync<Envelope<VideoStatusResponse>>(
                JsonOptions,
                cancellationToken);
            var status = envelope?.Data
                ?? throw new InvalidDataException("HeyGen presenter status returned no data.");
            if (string.Equals(status.Status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(status.VideoUrl) || status.Duration is null or <= 0)
                {
                    throw new InvalidDataException("HeyGen completed a presenter without usable video metadata.");
                }

                return status;
            }

            if (string.Equals(status.Status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"HeyGen could not create the agent presenter ({Sanitize(status.FailureCode)}). The static agent photo will be used.");
            }

            if (!string.Equals(status.Status, "pending", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(status.Status, "processing", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("HeyGen returned an unknown presenter status.");
            }

            await Task.Delay(TimeSpan.FromSeconds(configuration.PollIntervalSeconds), cancellationToken);
        }
    }

    private async Task<MemoryStream> DownloadAsync(
        string url,
        int maximumMegabytes,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException("HeyGen returned an invalid presenter download URL.");
        }

        using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, "HeyGen presenter download", cancellationToken);
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(mediaType, "video/mp4", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("HeyGen did not return an MP4 presenter video.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        var result = new MemoryStream();
        var maximumBytes = checked(maximumMegabytes * 1024L * 1024L);
        var buffer = new byte[81_920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > maximumBytes)
            {
                throw new InvalidDataException("HeyGen presenter video exceeds the configured size limit.");
            }

            await result.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        if (total == 0)
        {
            throw new InvalidDataException("HeyGen returned an empty presenter video.");
        }

        result.Position = 0;
        return result;
    }

    private static HttpRequestMessage Request(
        HttpMethod method,
        HeyGenOptions configuration,
        string path,
        string? idempotencyKey = null)
    {
        var baseUri = new Uri(configuration.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        var message = new HttpRequestMessage(method, new Uri(baseUri, path.TrimStart('/')));
        message.Headers.Add("X-Api-Key", configuration.ApiKey);
        if (idempotencyKey is not null)
        {
            message.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return message;
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

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        string? code = null;
        try
        {
            code = JsonSerializer.Deserialize<ErrorEnvelope>(body, JsonOptions)?.Error?.Code;
        }
        catch (JsonException)
        {
        }

        throw new HttpRequestException(
            $"{operation} failed with HTTP {(int)response.StatusCode} ({Sanitize(code)}).",
            null,
            response.StatusCode);
    }

    private static void Validate(HeyGenOptions configuration)
    {
        if (!configuration.Enabled)
        {
            throw new InvalidOperationException("HeyGen agent presenter generation is disabled.");
        }

        if (string.IsNullOrWhiteSpace(configuration.ApiKey)
            || string.IsNullOrWhiteSpace(configuration.Model)
            || string.IsNullOrWhiteSpace(configuration.GenerationVersion)
            || !Uri.TryCreate(configuration.BaseUrl, UriKind.Absolute, out var baseUri)
            || baseUri.Scheme != Uri.UriSchemeHttps
            || configuration.Resolution != "720p")
        {
            throw new InvalidOperationException("Enabled HeyGen presenter generation has invalid configuration.");
        }

        if (configuration.IntroMaximumSeconds is < 1 or > 10
            || configuration.OutroMaximumSeconds is < 0 or > 10
            || configuration.IntroMaximumSeconds + configuration.OutroMaximumSeconds > 15
            || configuration.PollIntervalSeconds is < 1 or > 60
            || configuration.RequestTimeoutSeconds is < 60 or > 3_600
            || configuration.MaxOutputMegabytes is < 1 or > 500
            || configuration.EstimatedCostPerMinuteUsd is < 0 or > 100)
        {
            throw new InvalidOperationException("HeyGen presenter limits are outside supported bounds.");
        }
    }

    private static void Validate(AgentAvatarVideoProviderRequest request)
    {
        if (!request.Portrait.CanRead || !request.Audio.CanRead
            || request.PortraitContentType is not ("image/jpeg" or "image/png" or "image/webp")
            || request.AudioContentType is not ("audio/mpeg" or "audio/wav" or "audio/x-wav")
            || request.DurationMs is < 500 or > 15_000
            || request.IdempotencyKey.Length != 64
            || request.IdempotencyKey.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("The HeyGen presenter request is outside the supported input contract.");
        }
    }

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "provider_error";
        }

        var normalized = new string(value.Where(character => char.IsLetterOrDigit(character) || character is '_' or '-').ToArray());
        return normalized.Length is > 0 and <= 80 ? normalized : "provider_error";
    }

    private sealed record Envelope<T>(T? Data);
    private sealed record UploadAssetResponse([property: JsonPropertyName("asset_id")] string AssetId);
    private sealed record CreateVideoResponse([property: JsonPropertyName("video_id")] string VideoId);
    private sealed record VideoStatusResponse(
        string Status,
        [property: JsonPropertyName("video_url")] string? VideoUrl,
        decimal? Duration,
        [property: JsonPropertyName("failure_code")] string? FailureCode);
    private sealed record ErrorEnvelope(ErrorBody? Error);
    private sealed record ErrorBody(string? Code);
}
