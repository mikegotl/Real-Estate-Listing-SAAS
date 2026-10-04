using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ListingStudio.AI.Configuration;
using ListingStudio.Application.Audio;
using Microsoft.Extensions.Options;

namespace ListingStudio.AI.Audio;

public sealed class ElevenLabsVoiceProvider(
    HttpClient httpClient,
    IOptions<VoiceOptions> options) : IVoiceProvider
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public string GenerationVersion
    {
        get
        {
            var configuration = options.Value;
            var voiceHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(configuration.VoiceId)))[..16].ToLowerInvariant();
            return $"elevenlabs-v1/{configuration.ModelId}/{configuration.OutputFormat}/voice-{voiceHash}";
        }
    }

    public async Task<VoiceGenerationResult> GenerateAsync(
        VoiceGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var configuration = options.Value;
        ValidateConfiguration(configuration);
        var script = BuildScript(request);
        var endpoint = BuildEndpoint(configuration);

        HttpResponseMessage? response = null;
        try
        {
            for (var attempt = 1; attempt <= configuration.MaxRetryAttempts; attempt++)
            {
                response = await SendAsync(endpoint, configuration, script.Text, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return await ReadResultAsync(response, script, configuration.OutputFormat, cancellationToken);
                }

                if (!IsTransient(response.StatusCode) || attempt == configuration.MaxRetryAttempts)
                {
                    throw CreateProviderException(response);
                }

                response.Dispose();
                response = null;
                var delay = TimeSpan.FromMilliseconds(
                    configuration.RetryBaseDelayMilliseconds * Math.Pow(2, attempt - 1));
                await Task.Delay(delay, cancellationToken);
            }

            throw new InvalidOperationException("ElevenLabs retry processing ended unexpectedly.");
        }
        finally
        {
            response?.Dispose();
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        Uri endpoint,
        VoiceOptions configuration,
        string text,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new { text, model_id = configuration.ModelId }),
        };
        message.Headers.Add("xi-api-key", configuration.ApiKey);
        return await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static async Task<VoiceGenerationResult> ReadResultAsync(
        HttpResponseMessage response,
        SpeechScript script,
        string outputFormat,
        CancellationToken cancellationToken)
    {
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<ElevenLabsResponse>(
            responseStream,
            SerializerOptions,
            cancellationToken) ?? throw new InvalidDataException("ElevenLabs returned an empty response.");
        byte[] audio;
        try
        {
            audio = Convert.FromBase64String(payload.AudioBase64);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("ElevenLabs returned invalid base64 audio.", exception);
        }

        if (audio.Length == 0)
        {
            throw new InvalidDataException("ElevenLabs returned empty audio.");
        }

        var alignment = payload.Alignment ?? payload.NormalizedAlignment
            ?? throw new InvalidDataException("ElevenLabs returned no timing alignment.");
        var timing = MapTiming(script, alignment);
        var durationMs = timing.Characters.Count == 0 ? 0 : timing.Characters.Max(mark => mark.EndMs);
        if (durationMs <= 0)
        {
            throw new InvalidDataException("ElevenLabs returned an invalid audio duration.");
        }

        var (contentType, extension) = GetMediaType(outputFormat);
        return new VoiceGenerationResult(audio, contentType, extension, durationMs, timing);
    }

    private static VoiceTimingMetadata MapTiming(SpeechScript script, ElevenLabsAlignment alignment)
    {
        var count = alignment.Characters.Count;
        if (alignment.CharacterStartTimesSeconds.Count != count
            || alignment.CharacterEndTimesSeconds.Count != count)
        {
            throw new InvalidDataException("ElevenLabs timing alignment does not match the submitted narration.");
        }

        var characters = new VoiceCharacterTiming[count];
        var characterOffsets = new int[count + 1];
        for (var index = 0; index < count; index++)
        {
            if (alignment.Characters[index].Length == 0)
            {
                throw new InvalidDataException("ElevenLabs returned an empty alignment character.");
            }

            var startMs = ToMilliseconds(alignment.CharacterStartTimesSeconds[index]);
            var endMs = ToMilliseconds(alignment.CharacterEndTimesSeconds[index]);
            if (startMs < 0 || endMs < startMs)
            {
                throw new InvalidDataException("ElevenLabs returned invalid character timing.");
            }

            characters[index] = new VoiceCharacterTiming(alignment.Characters[index], startMs, endMs);
            characterOffsets[index + 1] = characterOffsets[index] + alignment.Characters[index].Length;
        }

        if (!string.Equals(string.Concat(alignment.Characters), script.Text, StringComparison.Ordinal))
        {
            throw new InvalidDataException("ElevenLabs timing characters do not match the submitted narration.");
        }

        var segments = script.Ranges
            .Select(range => MapSegmentTiming(range, characters, characterOffsets))
            .ToArray();
        return new VoiceTimingMetadata(characters, segments);
    }

    private static VoiceSegmentTiming MapSegmentTiming(
        SegmentRange range,
        VoiceCharacterTiming[] characters,
        int[] characterOffsets)
    {
        var startIndex = -1;
        var endIndex = -1;
        var endOffset = range.StartIndex + range.Length;
        for (var index = 0; index < characters.Length; index++)
        {
            if (characterOffsets[index] == range.StartIndex)
            {
                startIndex = index;
            }

            if (characterOffsets[index + 1] == endOffset)
            {
                endIndex = index;
                break;
            }
        }

        if (startIndex < 0 || endIndex < startIndex)
        {
            throw new InvalidDataException("ElevenLabs segment timing does not align with the submitted narration.");
        }

        return new VoiceSegmentTiming(
            range.SegmentId,
            characters[startIndex].StartMs,
            characters[endIndex].EndMs);
    }

    private static SpeechScript BuildScript(VoiceGenerationRequest request)
    {
        if (request.Segments.Count == 0)
        {
            throw new ArgumentException("At least one narration segment is required.", nameof(request));
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var builder = new System.Text.StringBuilder();
        var ranges = new List<SegmentRange>(request.Segments.Count);
        foreach (var segment in request.Segments)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(segment.Id);
            ArgumentException.ThrowIfNullOrWhiteSpace(segment.Text);
            if (!ids.Add(segment.Id))
            {
                throw new ArgumentException("Narration segment IDs must be unique.", nameof(request));
            }

            if (segment.PlannedStartMs < 0 || segment.PlannedDurationMs <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(request), "Narration timing must be positive.");
            }

            if (builder.Length > 0)
            {
                builder.Append("\n\n");
            }

            var text = segment.Text.Trim();
            var start = builder.Length;
            builder.Append(text);
            ranges.Add(new SegmentRange(segment.Id, start, text.Length));
        }

        return new SpeechScript(builder.ToString(), ranges);
    }

    private static void ValidateConfiguration(VoiceOptions configuration)
    {
        if (!string.Equals(configuration.Provider, "ElevenLabs", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Voice:Provider must be ElevenLabs for narration generation.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(configuration.ApiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration.VoiceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration.ModelId);
        if (configuration.MaxRetryAttempts is < 1 or > 5)
        {
            throw new InvalidOperationException("Voice:MaxRetryAttempts must be between 1 and 5.");
        }

        if (configuration.RetryBaseDelayMilliseconds is < 0 or > 10_000)
        {
            throw new InvalidOperationException("Voice:RetryBaseDelayMilliseconds must be between 0 and 10000.");
        }

        if (!configuration.OutputFormat.StartsWith("mp3_", StringComparison.Ordinal)
            || configuration.OutputFormat.Any(character => !char.IsLetterOrDigit(character) && character != '_'))
        {
            throw new InvalidOperationException("Voice:OutputFormat must be a supported MP3 output format.");
        }
    }

    private static Uri BuildEndpoint(VoiceOptions configuration)
    {
        if (!Uri.TryCreate(configuration.Endpoint, UriKind.Absolute, out var baseUri)
            || baseUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Voice:Endpoint must be an absolute HTTPS URL.");
        }

        var endpoint = $"{baseUri.AbsoluteUri.TrimEnd('/')}/{Uri.EscapeDataString(configuration.VoiceId)}"
            + $"/with-timestamps?output_format={Uri.EscapeDataString(configuration.OutputFormat)}";
        return new Uri(endpoint, UriKind.Absolute);
    }

    private static bool IsTransient(HttpStatusCode statusCode) => statusCode is HttpStatusCode.RequestTimeout
        or HttpStatusCode.TooManyRequests
        or HttpStatusCode.InternalServerError
        or HttpStatusCode.BadGateway
        or HttpStatusCode.ServiceUnavailable
        or HttpStatusCode.GatewayTimeout;

    private static HttpRequestException CreateProviderException(HttpResponseMessage response)
    {
        var requestId = response.Headers.TryGetValues("request-id", out var values)
            ? values.FirstOrDefault()
            : null;
        return new HttpRequestException(
            requestId is null
                ? $"ElevenLabs narration generation failed with HTTP {(int)response.StatusCode}."
                : $"ElevenLabs narration generation failed with HTTP {(int)response.StatusCode} (request {requestId}).",
            null,
            response.StatusCode);
    }

    private static int ToMilliseconds(decimal seconds) => checked((int)Math.Ceiling(seconds * 1_000m));

    private static (string ContentType, string Extension) GetMediaType(string outputFormat) =>
        outputFormat.StartsWith("mp3_", StringComparison.Ordinal)
            ? ("audio/mpeg", ".mp3")
            : throw new InvalidOperationException(
                string.Format(CultureInfo.InvariantCulture, "Unsupported voice output format: {0}.", outputFormat));

    private sealed record SpeechScript(string Text, IReadOnlyList<SegmentRange> Ranges);

    private sealed record SegmentRange(string SegmentId, int StartIndex, int Length);

    private sealed record ElevenLabsResponse(
        [property: JsonPropertyName("audio_base64")] string AudioBase64,
        ElevenLabsAlignment? Alignment,
        [property: JsonPropertyName("normalized_alignment")] ElevenLabsAlignment? NormalizedAlignment);

    private sealed record ElevenLabsAlignment(
        IReadOnlyList<string> Characters,
        [property: JsonPropertyName("character_start_times_seconds")] IReadOnlyList<decimal> CharacterStartTimesSeconds,
        [property: JsonPropertyName("character_end_times_seconds")] IReadOnlyList<decimal> CharacterEndTimesSeconds);
}
