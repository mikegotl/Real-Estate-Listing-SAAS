using System.Net;
using System.Text;
using ListingStudio.Application.Videos;
using ListingStudio.Video.Configuration;
using ListingStudio.Video.Generation;
using Microsoft.Extensions.Options;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class HeyGenAgentAvatarVideoProviderTests
{
    [Fact]
    public async Task GenerateUploadsApprovedAssetsCreatesOneIdempotentVideoAndDownloadsIt()
    {
        var handler = new RoutingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v3/assets" => Json("""{"data":{"asset_id":"asset-123","url":"https://files.heygen.test/a","mime_type":"image/png","size_bytes":3}}"""),
            "/v3/videos" when request.Method == HttpMethod.Post => Json("""{"data":{"video_id":"video-123","status":"pending"}}"""),
            "/v3/videos/video-123" => Json("""{"data":{"id":"video-123","status":"completed","video_url":"https://files.heygen.test/video.mp4","duration":10.0}}"""),
            "/video.mp4" => Video([0, 0, 0, 20, 102, 116, 121, 112]),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var client = new HttpClient(handler);
        var provider = CreateProvider(client, enabled: true);
        await using var portrait = new MemoryStream([1, 2, 3]);
        await using var audio = new MemoryStream([4, 5, 6]);

        var result = await provider.GenerateAsync(new AgentAvatarVideoProviderRequest(
            "agent.png",
            "image/png",
            portrait,
            "presenter.mp3",
            "audio/mpeg",
            audio,
            10_000,
            new string('a', 64)));

        await using var content = result.Content;
        Assert.Equal("HeyGen", result.Provider);
        Assert.Equal("avatar_iv", result.Model);
        Assert.Equal("video-123", result.ProviderRequestId);
        Assert.Equal(0.385m, result.EstimatedCostUsd);
        Assert.Equal(720, result.Width);
        Assert.Equal(720, result.Height);
        Assert.Equal(10_000, result.DurationMs);
        Assert.Equal(5, handler.Requests.Count);
        Assert.All(handler.Requests.Take(4), request => Assert.Equal("test-api-key", request.ApiKey));
        Assert.Null(handler.Requests[4].ApiKey);
        Assert.Equal(new string('a', 64) + ":portrait", handler.Requests[0].IdempotencyKey);
        Assert.Equal(new string('a', 64) + ":audio", handler.Requests[1].IdempotencyKey);
        Assert.Equal(new string('a', 64), handler.Requests[2].IdempotencyKey);
        Assert.Contains("\"type\":\"image\"", handler.Requests[2].Body, StringComparison.Ordinal);
        Assert.Contains("\"audio_asset_id\":\"asset-123\"", handler.Requests[2].Body, StringComparison.Ordinal);
        Assert.Contains("\"aspect_ratio\":\"1:1\"", handler.Requests[2].Body, StringComparison.Ordinal);
        Assert.Contains("\"resolution\":\"720p\"", handler.Requests[2].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisabledProviderDoesNotSendPaidRequests()
    {
        var handler = new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(handler);
        var provider = CreateProvider(client, enabled: false);
        await using var portrait = new MemoryStream([1]);
        await using var audio = new MemoryStream([2]);

        Assert.False(provider.IsEnabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GenerateAsync(
            new AgentAvatarVideoProviderRequest(
                "agent.png", "image/png", portrait, "presenter.mp3", "audio/mpeg", audio,
                1_000, new string('a', 64))));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task FailedGenerationReturnsSanitizedProviderReason()
    {
        var handler = new RoutingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v3/assets" => Json("""{"data":{"asset_id":"asset-123","url":"https://files.heygen.test/a","mime_type":"image/png","size_bytes":3}}"""),
            "/v3/videos" when request.Method == HttpMethod.Post => Json("""{"data":{"video_id":"video-123","status":"pending"}}"""),
            "/v3/videos/video-123" => Json("""{"data":{"id":"video-123","status":"failed","failure_code":"face_not_detected","failure_message":"private detail"}}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var client = new HttpClient(handler);
        var provider = CreateProvider(client, enabled: true);
        await using var portrait = new MemoryStream([1]);
        await using var audio = new MemoryStream([2]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GenerateAsync(
            new AgentAvatarVideoProviderRequest(
                "agent.png", "image/png", portrait, "presenter.mp3", "audio/mpeg", audio,
                1_000, new string('a', 64))));

        Assert.Contains("face_not_detected", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private detail", exception.Message, StringComparison.Ordinal);
    }

    private static HeyGenAgentAvatarVideoProvider CreateProvider(HttpClient client, bool enabled) => new(
        client,
        Options.Create(new HeyGenOptions
        {
            Enabled = enabled,
            ApiKey = "test-api-key",
            BaseUrl = "https://api.heygen.test",
            Model = "avatar_iv",
            GenerationVersion = "test-v1",
            Resolution = "720p",
            IntroMaximumSeconds = 6,
            OutroMaximumSeconds = 4,
            PollIntervalSeconds = 1,
            RequestTimeoutSeconds = 60,
            MaxOutputMegabytes = 1,
            EstimatedCostPerMinuteUsd = 2.31m,
        }));

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Video(byte[] content)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
        response.Content.Headers.ContentType = new("video/mp4");
        return response;
    }

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri!,
                request.Headers.TryGetValues("X-Api-Key", out var keys) ? keys.SingleOrDefault() : null,
                request.Headers.TryGetValues("Idempotency-Key", out var ids) ? ids.SingleOrDefault() : null,
                body));
            return route(request);
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri Uri,
        string? ApiKey,
        string? IdempotencyKey,
        string Body);
}
