using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Properties;
using ListingStudio.Video.Configuration;
using Microsoft.Extensions.Options;

namespace ListingStudio.Video.Generation;

/// <summary>
/// Higgsfield image-to-video with a first and last frame. Each listing photo is uploaded through a pre-signed URL,
/// the generation request is queued, and the status endpoint is polled until the MP4 is ready.
/// </summary>
public sealed partial class HiggsfieldWalkthroughVideoProvider(
    HttpClient httpClient,
    IOptions<HiggsfieldOptions> options) : IWalkthroughVideoProvider
{
    public const string ProviderName = "higgsfield";

    public const string Prompt = """
        A smooth, steady first-person walk-through of a real home for a real-estate listing video.
        The camera starts on the first frame and moves naturally, at walking pace, to arrive exactly on the last frame,
        as if a buyer were walking from the first space into the next one.
        Preserve the architecture, rooms, furniture, windows, doors, fixtures, finishes and landscaping exactly as shown
        in the two photographs. Do not add, remove or redesign anything.
        Realistic real-estate photography. No people. No text. No camera shake.
        """;

    /// <summary>
    /// The prompt for one clip. Without a direction the model infers the path from the two frames and can turn the
    /// wrong way, so a direction set on the walkthrough map is spelled out from the buyer's point of view.
    /// </summary>
    public static string PromptFor(WalkthroughDirection? direction) => direction switch
    {
        null => Prompt,
        WalkthroughDirection.Ahead => Prompt + DirectionLine(
            "straight ahead, through the opening in front of the camera. Keep moving forward; do not turn left or right."),
        WalkthroughDirection.Left => Prompt + DirectionLine(
            "to the LEFT. The camera turns left (pans toward the left side of the first frame) while walking forward; "
            + "it must not turn right."),
        WalkthroughDirection.Right => Prompt + DirectionLine(
            "to the RIGHT. The camera turns right (pans toward the right side of the first frame) while walking forward; "
            + "it must not turn left."),
        WalkthroughDirection.Upstairs => Prompt + DirectionLine(
            "UPSTAIRS. The camera climbs the stairs, moving upward, to reach the upper floor."),
        WalkthroughDirection.Downstairs => Prompt + DirectionLine(
            "DOWNSTAIRS. The camera descends the stairs, moving downward, to reach the lower floor."),
        _ => throw new ArgumentOutOfRangeException(nameof(direction)),
    };

    /// <summary>
    /// The drone intro prompt. The clip starts on the real Exterior front photo and climbs away from it; the Worker
    /// plays it backwards, so the buyer sees a descent that lands on the photo. Keeping the camera on the facade and
    /// naming what not to invent limits how much of the roof, yard and street the model makes up.
    /// </summary>
    public static string AerialIntroPrompt(int heightFeet) => $"""
        Real-estate aerial drone shot. The camera starts exactly on this photograph of the front of the house,
        then smoothly rises and pulls back over the front yard toward the street, climbing to about {heightFeet} feet,
        tilting down slightly so the house stays centered in frame.
        Keep the facade, windows, doors, garage, colors, driveway and landscaping exactly as in the photograph.
        Show only a little of the roof. Do not add a swimming pool, water, a view, solar panels, extra buildings,
        people, moving cars or text. Steady, smooth, realistic drone footage. No camera shake.
        """;

    private static string DirectionLine(string movement) =>
        $"\nDirection: the next space is {movement}";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public bool IsEnabled => options.Value.Enabled;

    public int ClipDurationMs => options.Value.ClipDurationSeconds * 1_000;

    public string GenerationVersion
    {
        get
        {
            var configuration = options.Value;
            var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{configuration.Model}\n{configuration.ClipDurationSeconds}\n{Prompt}")))[..12].ToLowerInvariant();
            return $"higgsfield/{configuration.GenerationVersion}/{identity}";
        }
    }

    public string AerialIntroGenerationVersion
    {
        get
        {
            var configuration = options.Value;
            var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{configuration.Model}\n{configuration.ClipDurationSeconds}\n{AerialIntroPrompt(0)}")))[..12].ToLowerInvariant();
            return $"higgsfield/{configuration.GenerationVersion}/aerial-{identity}";
        }
    }

    public async Task<string> SubmitAerialIntroAsync(
        AerialIntroVideoSubmission submission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (submission.HeightFeet is < 20 or > 400)
        {
            throw new ArgumentOutOfRangeException(nameof(submission), "The drone height must be between 20 and 400 feet.");
        }

        var configuration = EnsureEnabled();
        var startUrl = await UploadAsync(configuration, submission.Frame, cancellationToken);
        return await QueueAsync(
            configuration,
            new GenerationRequest(
                AerialIntroPrompt(submission.HeightFeet),
                startUrl,
                null,
                configuration.ClipDurationSeconds,
                "off"),
            cancellationToken);
    }

    public async Task<string> SubmitAsync(
        WalkthroughVideoSubmission submission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        var configuration = EnsureEnabled();
        var startUrl = await UploadAsync(configuration, submission.StartFrame, cancellationToken);
        var endUrl = await UploadAsync(configuration, submission.EndFrame, cancellationToken);
        return await QueueAsync(
            configuration,
            new GenerationRequest(PromptFor(submission.Direction), startUrl, endUrl, configuration.ClipDurationSeconds, "off"),
            cancellationToken);
    }

    private async Task<string> QueueAsync(
        HiggsfieldOptions configuration,
        GenerationRequest request,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, ApiUri(configuration, configuration.Model.Trim('/')))
        {
            Content = JsonContent.Create(request, options: SerializerOptions),
        };
        var queued = await SendForJsonAsync<QueuedResponse>(configuration, message, cancellationToken);
        return IsRequestId(queued?.RequestId)
            ? queued!.RequestId!
            : throw new InvalidDataException("Higgsfield did not return a valid request ID.");
    }

    public async Task<WalkthroughVideoPollResult> PollAsync(
        string providerRequestId,
        CancellationToken cancellationToken = default)
    {
        var configuration = EnsureEnabled();
        if (!IsRequestId(providerRequestId))
        {
            throw new ArgumentException("The Higgsfield request ID is invalid.", nameof(providerRequestId));
        }

        using var message = new HttpRequestMessage(
            HttpMethod.Get,
            ApiUri(configuration, $"requests/{providerRequestId}/status"));
        var status = await SendForJsonAsync<StatusResponse>(configuration, message, cancellationToken)
            ?? throw new InvalidDataException("Higgsfield returned an empty status.");
        switch (status.Status?.ToLowerInvariant())
        {
            case "queued" or "in_progress" or "processing":
                return new WalkthroughVideoPollResult(
                    WalkthroughVideoState.InProgress, null, ProviderName, configuration.Model, null);
            case "completed":
                var videoUrl = status.Video?.Url;
                if (!Uri.TryCreate(videoUrl, UriKind.Absolute, out var videoUri) || videoUri.Scheme != Uri.UriSchemeHttps)
                {
                    throw new InvalidDataException("Higgsfield completed without an HTTPS video URL.");
                }

                var content = await DownloadAsync(configuration, videoUri, cancellationToken);
                return new WalkthroughVideoPollResult(
                    WalkthroughVideoState.Completed, content, ProviderName, configuration.Model, null);
            case "failed":
                return Failed(configuration, "Higgsfield could not generate this walk-through.");
            case "nsfw":
                return Failed(configuration, "Higgsfield's content filter rejected these photos.");
            case "canceled" or "cancelled":
                return Failed(configuration, "The Higgsfield request was canceled.");
            default:
                throw new InvalidDataException("Higgsfield returned an unknown request status.");
        }
    }

    private static WalkthroughVideoPollResult Failed(HiggsfieldOptions configuration, string reason) =>
        new(WalkthroughVideoState.Failed, null, ProviderName, configuration.Model, reason);

    private async Task<string> UploadAsync(
        HiggsfieldOptions configuration,
        WalkthroughVideoFrame frame,
        CancellationToken cancellationToken)
    {
        if (!frame.Content.CanRead || frame.ContentType is not ("image/jpeg" or "image/png" or "image/webp"))
        {
            throw new ArgumentException("Walk-through frames must be readable JPEG, PNG, or WEBP images.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, ApiUri(configuration, "files/generate-upload-url"))
        {
            Content = JsonContent.Create(new UploadUrlRequest(frame.ContentType), options: SerializerOptions),
        };
        var upload = await SendForJsonAsync<UploadUrlResponse>(configuration, request, cancellationToken);
        if (!Uri.TryCreate(upload?.UploadUrl, UriKind.Absolute, out var uploadUri)
            || uploadUri.Scheme != Uri.UriSchemeHttps
            || !Uri.TryCreate(upload.PublicUrl, UriKind.Absolute, out var publicUri)
            || publicUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException("Higgsfield did not return HTTPS upload URLs.");
        }

        // The pre-signed URL carries its own authorization, so the API key is never sent to the storage host.
        using var put = new HttpRequestMessage(HttpMethod.Put, uploadUri) { Content = new StreamContent(frame.Content) };
        put.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(frame.ContentType);
        foreach (var (name, value) in upload.UploadHeaders ?? [])
        {
            if (!string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                put.Headers.TryAddWithoutValidation(name, value);
            }
        }

        using var response = await SendAsync(configuration, put, cancellationToken);
        EnsureSuccess(response, "upload a walk-through frame");
        return publicUri.AbsoluteUri;
    }

    private async Task<Stream> DownloadAsync(
        HiggsfieldOptions configuration,
        Uri videoUri,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, videoUri);
        using var response = await SendAsync(configuration, message, cancellationToken, bufferContent: false);
        EnsureSuccess(response, "download the walk-through clip");
        var maximumBytes = checked(configuration.MaxOutputMegabytes * 1024L * 1024L);
        if (response.Content.Headers.ContentLength > maximumBytes)
        {
            throw new InvalidDataException("The walk-through clip exceeds the configured size limit.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        var output = new MemoryStream();
        var buffer = new byte[81_920];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (output.Length + read > maximumBytes)
            {
                throw new InvalidDataException("The walk-through clip exceeds the configured size limit.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        if (output.Length == 0)
        {
            throw new InvalidDataException("Higgsfield returned an empty walk-through clip.");
        }

        output.Position = 0;
        return output;
    }

    private async Task<T?> SendForJsonAsync<T>(
        HiggsfieldOptions configuration,
        HttpRequestMessage message,
        CancellationToken cancellationToken)
    {
        message.Headers.Authorization = new AuthenticationHeaderValue(
            "Key",
            $"{configuration.ApiKey}:{configuration.ApiSecret}");
        using var response = await SendAsync(configuration, message, cancellationToken);
        EnsureSuccess(response, "reach the Higgsfield API");
        return await response.Content.ReadFromJsonAsync<T>(SerializerOptions, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HiggsfieldOptions configuration,
        HttpRequestMessage message,
        CancellationToken cancellationToken,
        bool bufferContent = true)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(configuration.RequestTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            if (bufferContent)
            {
                await response.Content.LoadIntoBufferAsync(linked.Token);
            }

            return response;
        }
        catch (OperationCanceledException exception) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The Higgsfield request timed out.", exception);
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response, string action)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Could not {action}: Higgsfield returned HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }
    }

    private HiggsfieldOptions EnsureEnabled()
    {
        var configuration = options.Value;
        if (!configuration.Enabled)
        {
            throw new InvalidOperationException("Higgsfield walk-through generation is disabled.");
        }

        if (string.IsNullOrWhiteSpace(configuration.ApiKey)
            || string.IsNullOrWhiteSpace(configuration.ApiSecret)
            || string.IsNullOrWhiteSpace(configuration.Model)
            || !ModelPattern().IsMatch(configuration.Model)
            || configuration.Model.Contains("..", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(configuration.GenerationVersion)
            || configuration.GenerationVersion.Length > 40
            || !Uri.TryCreate(configuration.BaseUrl, UriKind.Absolute, out var baseUrl)
            || baseUrl.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                "Enabled Higgsfield requires ApiKey, ApiSecret, a model endpoint, GenerationVersion, and an HTTPS BaseUrl.");
        }

        if (configuration.ClipDurationSeconds is < 3 or > 15
            || configuration.RequestTimeoutSeconds is < 10 or > 900
            || configuration.MaxOutputMegabytes is < 1 or > 500)
        {
            throw new InvalidOperationException("Higgsfield clip length, timeout, or output-size configuration is invalid.");
        }

        return configuration;
    }

    private static Uri ApiUri(HiggsfieldOptions configuration, string path) =>
        new($"{configuration.BaseUrl.TrimEnd('/')}/{path}", UriKind.Absolute);

    private static bool IsRequestId(string? value) => value is not null && RequestIdPattern().IsMatch(value);

    [GeneratedRegex("^[A-Za-z0-9_-]{1,200}$")]
    private static partial Regex RequestIdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+)*$")]
    private static partial Regex ModelPattern();

    private sealed record UploadUrlRequest([property: JsonPropertyName("content_type")] string ContentType);

    private sealed record UploadUrlResponse(
        [property: JsonPropertyName("upload_url")] string? UploadUrl,
        [property: JsonPropertyName("public_url")] string? PublicUrl,
        [property: JsonPropertyName("upload_headers")] Dictionary<string, string>? UploadHeaders);

    private sealed record GenerationRequest(
        [property: JsonPropertyName("prompt")] string Prompt,
        [property: JsonPropertyName("image_url")] string ImageUrl,
        [property: JsonPropertyName("last_image_url"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LastImageUrl,
        [property: JsonPropertyName("duration")] int Duration,
        [property: JsonPropertyName("sound")] string Sound);

    private sealed record QueuedResponse([property: JsonPropertyName("request_id")] string? RequestId);

    private sealed record StatusResponse(
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("video")] VideoResponse? Video);

    private sealed record VideoResponse([property: JsonPropertyName("url")] string? Url);
}
