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

    private static readonly JsonSerializerOptions SerializerOptionsIgnoringNulls = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string GenerationVersion
    {
        get
        {
            var configuration = options.Value;
            var voices = string.Join(
                ';',
                configuration.ElevenLabsVoices
                    .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(entry => $"{entry.Key.ToLowerInvariant()}={entry.Value}"));
            var settings = string.Join(
                '|',
                configuration.VoiceId,
                voices,
                Number(configuration.Stability),
                Number(configuration.SimilarityBoost),
                Number(configuration.Style),
                Number(configuration.Speed),
                configuration.UseSpeakerBoost);
            var settingsHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(settings)))[..16].ToLowerInvariant();
            return $"elevenlabs-v2/{configuration.ModelId}/{configuration.OutputFormat}/voice-{settingsHash}";
        }
    }

    public async Task<VoiceGenerationResult> GenerateAsync(
        VoiceGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var configuration = options.Value;
        ValidateConfiguration(configuration);
        var voiceId = ResolveVoiceId(configuration, request.VoiceAccent);
        var endpoint = BuildEndpoint(configuration, voiceId);
        var scripts = BuildScripts(request, configuration.MaximumCharactersPerRequest);

        // A long script goes out in several requests. Each request's audio and timing are appended after the
        // previous ones, and the neighbouring text is sent along so the delivery carries across the joins.
        using var audio = new MemoryStream();
        var characters = new List<VoiceCharacterTiming>();
        var pieces = new List<(string SegmentId, int StartMs, int EndMs)>();
        var offsetMs = 0;
        var durationMs = 0;
        for (var index = 0; index < scripts.Count; index++)
        {
            var script = scripts[index];
            var result = await SynthesizeAsync(
                endpoint,
                configuration,
                script,
                index > 0 ? scripts[index - 1].Text : null,
                index + 1 < scripts.Count ? scripts[index + 1].Text : null,
                request.SpokenLanguageCode,
                cancellationToken);
            await audio.WriteAsync(result.Audio, cancellationToken);
            characters.AddRange(result.Timing.Characters.Select(mark => mark with
            {
                StartMs = mark.StartMs + offsetMs,
                EndMs = mark.EndMs + offsetMs,
            }));
            pieces.AddRange(result.Timing.Segments.Select(piece =>
                (piece.SegmentId, piece.StartMs + offsetMs, piece.EndMs + offsetMs)));
            var spokenMs = result.Timing.Characters.Count == 0 ? 0 : result.Timing.Characters.Max(mark => mark.EndMs);
            durationMs = offsetMs + spokenMs;
            offsetMs += Math.Max(spokenMs, AudioDurationMs(result.Audio.Length, configuration.OutputFormat));
        }

        if (durationMs <= 0)
        {
            throw new InvalidDataException("ElevenLabs returned an invalid audio duration.");
        }

        var segments = pieces
            .GroupBy(piece => piece.SegmentId, StringComparer.Ordinal)
            .Select(group => new VoiceSegmentTiming(
                group.Key,
                group.Min(piece => piece.StartMs),
                group.Max(piece => piece.EndMs)))
            .ToArray();
        var (contentType, extension) = GetMediaType(configuration.OutputFormat);
        return new VoiceGenerationResult(
            audio.ToArray(),
            contentType,
            extension,
            durationMs,
            new VoiceTimingMetadata(characters, segments));
    }

    private async Task<(byte[] Audio, VoiceTimingMetadata Timing)> SynthesizeAsync(
        Uri endpoint,
        VoiceOptions configuration,
        SpeechScript script,
        string? previousText,
        string? nextText,
        string languageCode,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;
        try
        {
            for (var attempt = 1; attempt <= configuration.MaxRetryAttempts; attempt++)
            {
                response = await SendAsync(
                    endpoint,
                    configuration,
                    script.Text,
                    previousText,
                    nextText,
                    languageCode,
                    cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return await ReadResultAsync(response, script, cancellationToken);
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

    private static string ResolveVoiceId(VoiceOptions configuration, string accent) =>
        !string.IsNullOrWhiteSpace(accent)
        && configuration.ElevenLabsVoices.TryGetValue(accent, out var voiceId)
        && !string.IsNullOrWhiteSpace(voiceId)
            ? voiceId
            : configuration.VoiceId;

    /// <summary>Constant-bitrate MP3 length from its size, e.g. mp3_44100_128 is 128 kbps.</summary>
    private static int AudioDurationMs(int bytes, string outputFormat)
    {
        var parts = outputFormat.Split('_');
        return parts.Length == 3 && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var kbps)
            && kbps > 0
                ? (int)((long)bytes * 8 / kbps)
                : 0;
    }

    /// <summary>Only these models accept a language_code; the others detect the language from the text.</summary>
    private static bool AcceptsLanguageCode(string modelId) =>
        modelId.Contains("v2_5", StringComparison.OrdinalIgnoreCase)
        || modelId.StartsWith("eleven_v3", StringComparison.OrdinalIgnoreCase);

    private static string Number(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private async Task<HttpResponseMessage> SendAsync(
        Uri endpoint,
        VoiceOptions configuration,
        string text,
        string? previousText,
        string? nextText,
        string languageCode,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new
            {
                text,
                model_id = configuration.ModelId,
                language_code = string.IsNullOrWhiteSpace(languageCode) || !AcceptsLanguageCode(configuration.ModelId)
                    ? null
                    : languageCode,
                voice_settings = new
                {
                    stability = configuration.Stability,
                    similarity_boost = configuration.SimilarityBoost,
                    style = configuration.Style,
                    use_speaker_boost = configuration.UseSpeakerBoost,
                    speed = configuration.Speed,
                },
                previous_text = previousText is null ? null : Tail(previousText),
                next_text = nextText is null ? null : Head(nextText),
            }, options: SerializerOptionsIgnoringNulls),
        };
        message.Headers.Add("xi-api-key", configuration.ApiKey);
        return await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static string Tail(string text) => text.Length <= 1_000 ? text : text[^1_000..];

    private static string Head(string text) => text.Length <= 1_000 ? text : text[..1_000];

    private static async Task<(byte[] Audio, VoiceTimingMetadata Timing)> ReadResultAsync(
        HttpResponseMessage response,
        SpeechScript script,
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
        return (audio, MapTiming(script, alignment));
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

    /// <summary>
    /// Packs the segments into as few requests as fit the character limit, keeping segments whole where possible
    /// and splitting an oversized one at sentence breaks. Segments are separated by a blank line, which the voice
    /// reads as a pause.
    /// </summary>
    private static List<SpeechScript> BuildScripts(VoiceGenerationRequest request, int maximumCharacters)
    {
        if (request.Segments.Count == 0)
        {
            throw new ArgumentException("At least one narration segment is required.", nameof(request));
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var pieces = new List<(string SegmentId, string Text)>();
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

            pieces.AddRange(SplitToFit(segment.Text.Trim(), maximumCharacters)
                .Select(text => (segment.Id, text)));
        }

        var scripts = new List<SpeechScript>();
        var builder = new StringBuilder();
        var ranges = new List<SegmentRange>();
        foreach (var (segmentId, text) in pieces)
        {
            if (builder.Length > 0 && builder.Length + 2 + text.Length > maximumCharacters)
            {
                scripts.Add(new SpeechScript(builder.ToString(), [.. ranges]));
                builder.Clear();
                ranges.Clear();
            }

            if (builder.Length > 0)
            {
                builder.Append("\n\n");
            }

            ranges.Add(new SegmentRange(segmentId, builder.Length, text.Length));
            builder.Append(text);
        }

        scripts.Add(new SpeechScript(builder.ToString(), [.. ranges]));
        return scripts;
    }

    private static IEnumerable<string> SplitToFit(string text, int maximumCharacters)
    {
        var remaining = text;
        while (remaining.Length > maximumCharacters)
        {
            var window = remaining[..maximumCharacters];
            var split = window.LastIndexOfAny(['.', '!', '?']) + 1;
            if (split < maximumCharacters / 2)
            {
                split = window.LastIndexOf(' ');
            }

            if (split < maximumCharacters / 2)
            {
                split = maximumCharacters;
            }

            yield return remaining[..split].Trim();
            remaining = remaining[split..].TrimStart();
        }

        if (remaining.Length > 0)
        {
            yield return remaining;
        }
    }

    private static void ValidateConfiguration(VoiceOptions configuration)
    {
        if (!string.Equals(configuration.Provider, "ElevenLabs", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Voice:Provider must be ElevenLabs for narration generation.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(configuration.ApiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration.VoiceId);
        if (configuration.MaximumCharactersPerRequest is < 500 or > 40_000)
        {
            throw new InvalidOperationException("Voice:MaximumCharactersPerRequest must be between 500 and 40000.");
        }

        if (configuration.Stability is < 0 or > 1
            || configuration.SimilarityBoost is < 0 or > 1
            || configuration.Style is < 0 or > 1
            || configuration.Speed is < 0.7m or > 1.2m)
        {
            throw new InvalidOperationException("Voice settings are outside the ranges ElevenLabs accepts.");
        }
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

    private static Uri BuildEndpoint(VoiceOptions configuration, string voiceId)
    {
        if (!Uri.TryCreate(configuration.Endpoint, UriKind.Absolute, out var baseUri)
            || baseUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Voice:Endpoint must be an absolute HTTPS URL.");
        }

        var endpoint = $"{baseUri.AbsoluteUri.TrimEnd('/')}/{Uri.EscapeDataString(voiceId)}"
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
