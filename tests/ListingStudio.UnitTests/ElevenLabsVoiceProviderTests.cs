using System.Net;
using System.Text;
using System.Text.Json;
using ListingStudio.AI.Audio;
using ListingStudio.AI.Configuration;
using ListingStudio.Application.Audio;
using Microsoft.Extensions.Options;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class ElevenLabsVoiceProviderTests
{
    [Fact]
    public async Task GenerateSendsConfiguredRequestAndMapsAudioAndTiming()
    {
        const string script = "Hello.\n\nWorld.";
        var handler = new RecordingHandler(Response(HttpStatusCode.OK, SuccessPayload(script)));
        using var httpClient = new HttpClient(handler);
        var provider = CreateProvider(httpClient);
        var request = new VoiceGenerationRequest(
        [
            new VoiceNarrationSegment("one", "Hello.", 0, 2_000),
            new VoiceNarrationSegment("two", "World.", 2_500, 2_000),
        ]);

        var result = await provider.GenerateAsync(request);

        Assert.Equal([1, 2, 3, 4], result.AudioData);
        Assert.Equal("audio/mpeg", result.ContentType);
        Assert.Equal(".mp3", result.FileExtension);
        Assert.Equal(1_400, result.DurationMs);
        Assert.Equal(14, result.Timing!.Characters.Count);
        Assert.Equal(new VoiceSegmentTiming("one", 0, 600), result.Timing.Segments[0]);
        Assert.Equal(new VoiceSegmentTiming("two", 800, 1_400), result.Timing.Segments[1]);
        Assert.StartsWith("elevenlabs-v2/test-model/mp3_44100_128/voice-", provider.GenerationVersion, StringComparison.Ordinal);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(
            "https://api.elevenlabs.test/v1/text-to-speech/test-voice/with-timestamps?output_format=mp3_44100_128",
            sent.RequestUri);
        Assert.Equal("test-api-key", sent.ApiKey);
        using var body = JsonDocument.Parse(sent.Body);
        Assert.Equal(script, body.RootElement.GetProperty("text").GetString());
        Assert.Equal("test-model", body.RootElement.GetProperty("model_id").GetString());
        var settings = body.RootElement.GetProperty("voice_settings");
        Assert.Equal(0.45m, settings.GetProperty("stability").GetDecimal());
        Assert.Equal(0.8m, settings.GetProperty("similarity_boost").GetDecimal());
        Assert.Equal(1.0m, settings.GetProperty("speed").GetDecimal());
        Assert.False(body.RootElement.TryGetProperty("language_code", out _));
        Assert.False(body.RootElement.TryGetProperty("previous_text", out _));
    }

    [Theory]
    [InlineData("British", "british-voice")]
    [InlineData("Castilian", "test-voice")]
    [InlineData("", "test-voice")]
    public async Task GenerateUsesTheVoiceConfiguredForTheAccent(string accent, string expectedVoice)
    {
        var handler = new RecordingHandler(Response(HttpStatusCode.OK, SuccessPayload("Hola.")));
        using var httpClient = new HttpClient(handler);
        var provider = CreateProvider(httpClient, voices: new() { ["British"] = "british-voice" });

        await provider.GenerateAsync(new VoiceGenerationRequest(
            [new VoiceNarrationSegment("one", "Hola.", 0, 2_000)], "es", VoiceAccent: accent));

        Assert.Contains($"/text-to-speech/{expectedVoice}/with-timestamps", Assert.Single(handler.Requests).RequestUri);
    }

    [Fact]
    public async Task GenerateSendsTheLanguageOnlyToModelsThatAcceptIt()
    {
        var handler = new RecordingHandler(Response(HttpStatusCode.OK, SuccessPayload("Hola.")));
        using var httpClient = new HttpClient(handler);
        var provider = CreateProvider(httpClient, modelId: "eleven_flash_v2_5");

        await provider.GenerateAsync(new VoiceGenerationRequest(
            [new VoiceNarrationSegment("one", "Hola.", 0, 2_000)], "es"));

        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        Assert.Equal("es", body.RootElement.GetProperty("language_code").GetString());
    }

    [Fact]
    public async Task GenerateSplitsALongScriptAndAppendsAudioAndTiming()
    {
        var first = new string('a', 400) + ".";
        var second = new string('b', 300) + ".";
        // The fake alignment is 0.1 s per character, so the first request speaks for 40.1 s; its four bytes of
        // audio are shorter, so the spoken length is the offset for the second request.
        var handler = new RecordingHandler(
            Response(HttpStatusCode.OK, SuccessPayload(first)),
            Response(HttpStatusCode.OK, SuccessPayload(second)));
        using var httpClient = new HttpClient(handler);
        var provider = CreateProvider(httpClient, maximumCharacters: 500);

        var result = await provider.GenerateAsync(new VoiceGenerationRequest(
        [
            new VoiceNarrationSegment("one", first, 0, 2_000),
            new VoiceNarrationSegment("two", second, 2_500, 2_000),
        ]));

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal([1, 2, 3, 4, 1, 2, 3, 4], result.AudioData);
        Assert.Equal(new VoiceSegmentTiming("one", 0, 40_100), result.Timing!.Segments[0]);
        Assert.Equal(new VoiceSegmentTiming("two", 40_100, 70_200), result.Timing.Segments[1]);
        Assert.Equal(70_200, result.DurationMs);
        Assert.Equal(first.Length + second.Length, result.Timing.Characters.Count);
        using var secondBody = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.Equal(first, secondBody.RootElement.GetProperty("previous_text").GetString());
        using var firstBody = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.Equal(second, firstBody.RootElement.GetProperty("next_text").GetString());
    }

    [Fact]
    public async Task GenerateRetriesTransientResponses()
    {
        const string script = "Hello.";
        var handler = new RecordingHandler(
            Response(HttpStatusCode.TooManyRequests, "{}"),
            Response(HttpStatusCode.OK, SuccessPayload(script)));
        using var httpClient = new HttpClient(handler);
        var provider = CreateProvider(httpClient);

        var result = await provider.GenerateAsync(new VoiceGenerationRequest(
            [new VoiceNarrationSegment("one", script, 0, 2_000)]));

        Assert.Equal(600, result.DurationMs);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task GenerateDoesNotRetryValidationErrorsOrExposeResponseBody()
    {
        var failure = Response(HttpStatusCode.UnprocessableEntity, "customer text must not appear");
        failure.Headers.Add("request-id", "provider-request-123");
        var handler = new RecordingHandler(failure);
        using var httpClient = new HttpClient(handler);
        var provider = CreateProvider(httpClient);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => provider.GenerateAsync(
            new VoiceGenerationRequest([new VoiceNarrationSegment("one", "Hello.", 0, 2_000)])));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, exception.StatusCode);
        Assert.Contains("provider-request-123", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("customer text", exception.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    private static ElevenLabsVoiceProvider CreateProvider(
        HttpClient client,
        Dictionary<string, string>? voices = null,
        string modelId = "test-model",
        int maximumCharacters = 9_000) => new(
        client,
        Options.Create(new VoiceOptions
        {
            Provider = "ElevenLabs",
            ApiKey = "test-api-key",
            VoiceId = "test-voice",
            ElevenLabsVoices = new(voices ?? [], StringComparer.OrdinalIgnoreCase),
            MaximumCharactersPerRequest = maximumCharacters,
            Endpoint = "https://api.elevenlabs.test/v1/text-to-speech",
            ModelId = modelId,
            OutputFormat = "mp3_44100_128",
            MaxRetryAttempts = 3,
            RetryBaseDelayMilliseconds = 1,
        }));

    private static HttpResponseMessage Response(HttpStatusCode statusCode, string content) => new(statusCode)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json"),
    };

    private static string SuccessPayload(string script)
    {
        var characters = script.Select(character => character.ToString()).ToArray();
        var starts = Enumerable.Range(0, characters.Length).Select(index => index / 10m).ToArray();
        var ends = Enumerable.Range(1, characters.Length).Select(index => index / 10m).ToArray();
        return JsonSerializer.Serialize(new
        {
            audio_base64 = Convert.ToBase64String([1, 2, 3, 4]),
            alignment = new
            {
                characters,
                character_start_times_seconds = starts,
                character_end_times_seconds = ends,
            },
            normalized_alignment = (object?)null,
        });
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
                request.Headers.GetValues("xi-api-key").Single(),
                await request.Content!.ReadAsStringAsync(cancellationToken)));
            Assert.True(queued.TryDequeue(out var response), "A fake provider response must be queued.");
            return response;
        }
    }

    private sealed record RecordedRequest(string RequestUri, string ApiKey, string Body);
}
