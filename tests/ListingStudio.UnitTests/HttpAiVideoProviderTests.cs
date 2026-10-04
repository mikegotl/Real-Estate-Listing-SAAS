using System.Net;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Videos;
using ListingStudio.Video.Configuration;
using ListingStudio.Video.Generation;
using Microsoft.Extensions.Options;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class HttpAiVideoProviderTests
{
    [Fact]
    public async Task GenerateSendsGuardrailedMultipartRequestAndMapsCostMetadata()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([0, 0, 0, 20, 102, 116, 121, 112]),
        };
        response.Content.Headers.ContentType = new("video/mp4");
        response.Headers.Add("X-Video-Width", "1920");
        response.Headers.Add("X-Video-Height", "1080");
        response.Headers.Add("X-Video-Duration-Ms", "5000");
        response.Headers.Add("X-Provider-Request-Id", "request-123");
        response.Headers.Add("X-Provider-Model", "provider-model-v2");
        response.Headers.Add("X-Estimated-Cost-Usd", "0.175");
        var handler = new RecordingHandler(response);
        using var client = new HttpClient(handler);
        var provider = CreateProvider(client, enabled: true);
        await using var image = new MemoryStream([1, 2, 3]);

        var result = await provider.GenerateAsync(new AiVideoProviderRequest(
            Guid.NewGuid(),
            "front.png",
            "image/png",
            image,
            "slow cinematic push forward",
            5_000,
            VideoAspectRatio.Landscape16By9,
            new string('a', 64)));

        await using var content = result.Content;
        Assert.Equal("video/mp4", result.ContentType);
        Assert.Equal("gateway", result.Provider);
        Assert.Equal("provider-model-v2", result.Model);
        Assert.Equal("request-123", result.ProviderRequestId);
        Assert.Equal(0.175m, result.EstimatedCostUsd);
        Assert.Equal(1_920, result.Width);
        Assert.Equal(1_080, result.Height);
        Assert.Contains("Preserve the architecture", handler.Body, StringComparison.Ordinal);
        Assert.Contains("slow cinematic push forward", handler.Body, StringComparison.Ordinal);
        Assert.Contains("No architectural hallucinations", handler.Body, StringComparison.Ordinal);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("test-api-key", handler.AuthorizationParameter);
        Assert.Equal(new string('a', 64), handler.IdempotencyKey);
    }

    [Fact]
    public async Task DisabledProviderCannotSpendOrSendARequest()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(handler);
        var provider = CreateProvider(client, enabled: false);
        await using var image = new MemoryStream([1]);

        Assert.False(provider.IsEnabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GenerateAsync(
            new AiVideoProviderRequest(
                Guid.NewGuid(), "front.png", "image/png", image,
                "slow cinematic push forward", 5_000, VideoAspectRatio.Landscape16By9, new string('a', 64))));
        Assert.Null(handler.Body);
    }

    private static HttpAiVideoProvider CreateProvider(HttpClient client, bool enabled) => new(
        client,
        Options.Create(new AIVideoOptions
        {
            Enabled = enabled,
            Provider = "gateway",
            ApiKey = "test-api-key",
            Endpoint = "https://ai-video.test/generate",
            Model = "configured-model",
            GenerationVersion = "gateway-v1",
            RequestTimeoutSeconds = 30,
            MaxOutputMegabytes = 1,
        }));

    private sealed class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }
        public string? IdempotencyKey { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            IdempotencyKey = request.Headers.GetValues("Idempotency-Key").Single();
            return response;
        }
    }
}
