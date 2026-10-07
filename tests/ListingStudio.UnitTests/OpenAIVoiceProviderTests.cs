using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ListingStudio.AI.Audio;
using ListingStudio.AI.Configuration;
using ListingStudio.Application.Audio;
using Microsoft.Extensions.Options;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class OpenAIVoiceProviderTests
{
    [Fact]
    public async Task GenerateSendsSpeechRequestAndReturnsWaveDuration()
    {
        var pcm = CreatePcm(durationMs: 1_000);
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(pcm)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") },
            },
        });
        using var httpClient = new HttpClient(handler);
        var provider = CreateProvider(httpClient);

        var result = await provider.GenerateAsync(new VoiceGenerationRequest(
        [
            new VoiceNarrationSegment("one", "Hello.", 500, 2_000),
            new VoiceNarrationSegment("two", "World.", 3_000, 2_000),
        ]));

        Assert.Equal(pcm.Length + 44, result.AudioData.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(result.AudioData, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(result.AudioData, 8, 4));
        Assert.Equal(pcm, result.AudioData[44..]);
        Assert.Equal("audio/wav", result.ContentType);
        Assert.Equal(".wav", result.FileExtension);
        Assert.Equal(1_000, result.DurationMs);
        Assert.Null(result.Timing);
        Assert.StartsWith("openai-tts-v2/test-tts/pcm-wrapped-wav/marin/instructions-", provider.GenerationVersion, StringComparison.Ordinal);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("https://api.openai.test/v1/audio/speech", sent.RequestUri);
        Assert.Equal("Bearer", sent.AuthorizationScheme);
        Assert.Equal("test-api-key", sent.AuthorizationParameter);
        using var body = JsonDocument.Parse(sent.Body);
        Assert.Equal("test-tts", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("marin", body.RootElement.GetProperty("voice").GetString());
        Assert.Equal("Hello.\n\nWorld.", body.RootElement.GetProperty("input").GetString());
        Assert.Equal("Speak clearly.", body.RootElement.GetProperty("instructions").GetString());
        Assert.Equal("pcm", body.RootElement.GetProperty("response_format").GetString());
    }

    [Fact]
    public async Task GenerateRejectsOversizedInputBeforeCallingProvider()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var provider = CreateProvider(httpClient);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GenerateAsync(
            new VoiceGenerationRequest(
                [new VoiceNarrationSegment("one", new string('a', 4_097), 0, 2_000)])));

        Assert.Contains("4096", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GenerateDoesNotExposeProviderResponseBody()
    {
        var failure = new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
        {
            Content = new StringContent("customer text must not appear", Encoding.UTF8, "application/json"),
        };
        failure.Headers.Add("x-request-id", "openai-request-123");
        var handler = new RecordingHandler(failure);
        using var httpClient = new HttpClient(handler);
        var provider = CreateProvider(httpClient);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => provider.GenerateAsync(
            new VoiceGenerationRequest([new VoiceNarrationSegment("one", "Hello.", 0, 2_000)])));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, exception.StatusCode);
        Assert.Contains("openai-request-123", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("customer text", exception.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GenerateRejectsMalformedPcmAudio()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3]),
        });
        using var httpClient = new HttpClient(handler);
        var provider = CreateProvider(httpClient);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => provider.GenerateAsync(
            new VoiceGenerationRequest([new VoiceNarrationSegment("one", "Hello.", 0, 2_000)])));

        Assert.Contains("PCM", exception.Message, StringComparison.Ordinal);
    }

    private static OpenAIVoiceProvider CreateProvider(HttpClient client) => new(
        client,
        Options.Create(new OpenAIOptions
        {
            ApiKey = "test-api-key",
            SpeechEndpoint = "https://api.openai.test/v1/audio/speech",
        }),
        Options.Create(new VoiceOptions
        {
            Provider = "OpenAI",
            OpenAIModel = "test-tts",
            OpenAIVoice = "marin",
            OpenAIInstructions = "Speak clearly.",
        }));

    private static byte[] CreatePcm(int durationMs)
    {
        const int sampleRate = 24_000;
        const short channels = 1;
        const short bitsPerSample = 16;
        var byteRate = sampleRate * channels * bitsPerSample / 8;
        return new byte[byteRate * durationMs / 1_000];
    }

    private sealed class RecordingHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> queued = new(responses);

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.RequestUri!.AbsoluteUri,
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter,
                await request.Content!.ReadAsStringAsync(cancellationToken)));
            Assert.True(queued.TryDequeue(out var response), "A fake provider response must be queued.");
            return response;
        }
    }

    private sealed record RecordedRequest(
        string RequestUri,
        string? AuthorizationScheme,
        string? AuthorizationParameter,
        string Body);
}
