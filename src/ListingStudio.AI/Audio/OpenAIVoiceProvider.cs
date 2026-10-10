using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using ListingStudio.AI.Configuration;
using ListingStudio.Application.Audio;
using Microsoft.Extensions.Options;

namespace ListingStudio.AI.Audio;

public sealed class OpenAIVoiceProvider(
    HttpClient httpClient,
    IOptions<OpenAIOptions> openAIOptions,
    IOptions<VoiceOptions> voiceOptions) : IVoiceProvider
{
    private const int MaximumInputCharacters = 4_096;
    private const int MaximumAudioBytes = 50 * 1024 * 1024;
    private const int PcmSampleRate = 24_000;
    private const short PcmChannels = 1;
    private const short PcmBitsPerSample = 16;
    private const int PcmByteRate = PcmSampleRate * PcmChannels * PcmBitsPerSample / 8;
    private const int PcmBlockAlign = PcmChannels * PcmBitsPerSample / 8;

    public string GenerationVersion
    {
        get
        {
            var voice = voiceOptions.Value;
            var instructionsHash = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(voice.OpenAIInstructions)))[..16].ToLowerInvariant();
            return $"openai-tts-v4/{voice.OpenAIModel}/segmented-chunked-pcm-wav/{voice.OpenAIVoice}/instructions-{instructionsHash}";
        }
    }

    public async Task<VoiceGenerationResult> GenerateAsync(
        VoiceGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var openAI = openAIOptions.Value;
        var voice = voiceOptions.Value;
        ValidateConfiguration(openAI, voice);
        var texts = ValidateSegments(request);

        // Each segment is synthesized on its own so the renderer can place it at its planned start, next to
        // the scene it describes. One continuous take drifts ahead of the visuals as soon as the voice reads
        // faster than the plan.
        using var pcm = new MemoryStream();
        var segments = new List<VoiceSegmentTiming>(texts.Count);
        for (var index = 0; index < texts.Count; index++)
        {
            var startMs = (int)(pcm.Length * 1_000L / PcmByteRate);
            foreach (var chunk in SplitToFit(texts[index]))
            {
                var segmentPcm = await SynthesizeAsync(openAI, voice, chunk, pcm.Length, cancellationToken);
                if (segmentPcm.Length % PcmBlockAlign != 0)
                {
                    throw new InvalidDataException("OpenAI returned malformed PCM narration audio.");
                }

                await pcm.WriteAsync(segmentPcm, cancellationToken);
            }

            var endMs = checked((int)Math.Ceiling(pcm.Length * 1_000d / PcmByteRate));
            segments.Add(new VoiceSegmentTiming(request.Segments[index].Id, startMs, endMs));
        }

        var audio = WrapPcmInWave(pcm.ToArray());
        var durationMs = checked((int)Math.Ceiling(pcm.Length * 1_000d / PcmByteRate));
        return new VoiceGenerationResult(
            audio,
            "audio/wav",
            ".wav",
            durationMs,
            new VoiceTimingMetadata([], segments));
    }

    private async Task<byte[]> SynthesizeAsync(
        OpenAIOptions openAI,
        VoiceOptions voice,
        string text,
        long bytesSoFar,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, openAI.SpeechEndpoint)
        {
            Content = JsonContent.Create(new
            {
                model = voice.OpenAIModel,
                voice = voice.OpenAIVoice,
                input = text,
                instructions = voice.OpenAIInstructions,
                response_format = "pcm",
            }),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", openAI.ApiKey);
        using var response = await httpClient.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var requestId = response.Headers.TryGetValues("x-request-id", out var values)
                ? values.FirstOrDefault()
                : null;
            throw new HttpRequestException(
                requestId is null
                    ? $"OpenAI narration generation failed with HTTP {(int)response.StatusCode}."
                    : $"OpenAI narration generation failed with HTTP {(int)response.StatusCode} (request {requestId}).",
                null,
                response.StatusCode);
        }

        return await ReadLimitedAsync(response, MaximumAudioBytes - bytesSoFar, cancellationToken);
    }

    private static List<string> ValidateSegments(VoiceGenerationRequest request)
    {
        if (request.Segments.Count == 0)
        {
            throw new ArgumentException("At least one narration segment is required.", nameof(request));
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var parts = new List<string>(request.Segments.Count);
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

            var text = segment.Text.Trim();
            parts.Add(text);
        }

        return parts;
    }

    private static IEnumerable<string> SplitToFit(string text)
    {
        var remaining = text.Trim();
        while (remaining.Length > MaximumInputCharacters)
        {
            var candidate = remaining[..MaximumInputCharacters];
            var split = candidate.LastIndexOfAny([' ', '\n', '\t']);
            if (split < MaximumInputCharacters / 2)
            {
                split = MaximumInputCharacters;
            }

            yield return remaining[..split].Trim();
            remaining = remaining[split..].TrimStart();
        }

        if (remaining.Length > 0)
        {
            yield return remaining;
        }
    }

    private static void ValidateConfiguration(OpenAIOptions openAI, VoiceOptions voice)
    {
        if (!string.Equals(voice.Provider, "OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Voice:Provider must be OpenAI for OpenAI narration generation.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(openAI.ApiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(voice.OpenAIModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(voice.OpenAIVoice);
        if (!Uri.TryCreate(openAI.SpeechEndpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("OpenAI:SpeechEndpoint must be an absolute HTTPS URL.");
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(
        HttpResponseMessage response,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[81_920];
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken);
            if (count == 0)
            {
                break;
            }

            if (output.Length + count > maximumBytes)
            {
                throw new InvalidDataException("OpenAI returned an oversized narration audio file.");
            }

            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }

        return output.Length == 0
            ? throw new InvalidDataException("OpenAI returned empty narration audio.")
            : output.ToArray();
    }

    private static byte[] WrapPcmInWave(byte[] pcm)
    {
        const int headerLength = 44;
        const short audioFormatPcm = 1;
        const int formatChunkSize = 16;
        if (pcm.Length % PcmBlockAlign != 0)
        {
            throw new InvalidDataException("OpenAI returned malformed PCM narration audio.");
        }

        var wave = new byte[checked(headerLength + pcm.Length)];
        var header = wave.AsSpan(0, headerLength);
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(4, 4), wave.Length - 8);
        "WAVE"u8.CopyTo(header[8..]);
        "fmt "u8.CopyTo(header[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(16, 4), formatChunkSize);
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(20, 2), audioFormatPcm);
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(22, 2), PcmChannels);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(24, 4), PcmSampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(28, 4), PcmByteRate);
        BinaryPrimitives.WriteInt16LittleEndian(
            header.Slice(32, 2),
            (short)PcmBlockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(34, 2), PcmBitsPerSample);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(40, 4), pcm.Length);
        pcm.CopyTo(wave, headerLength);
        return wave;
    }
}
