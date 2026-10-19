using System.Net;
using ListingStudio.Application.Administration;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class AiUsageHttpMessageHandlerTests
{
    [Fact]
    public async Task RecordsOnlySanitizedProviderDimensions()
    {
        var recorder = new CapturingRecorder();
        var handler = new AiUsageHttpMessageHandler(
            recorder,
            "OpenAI",
            "test-model",
            "test-operation",
            0.125m)
        {
            InnerHandler = new StubHandler(HttpStatusCode.OK),
        };
        using var client = new HttpClient(handler);

        using var response = await client.PostAsync(
            "https://provider.example/v1/responses?secret=query-value",
            new StringContent("raw prompt and customer data"));

        response.EnsureSuccessStatusCode();
        var observation = Assert.Single(recorder.Observations);
        Assert.Equal("OpenAI", observation.Provider);
        Assert.Equal("test-model", observation.Model);
        Assert.Equal("test-operation", observation.Operation);
        Assert.True(observation.Succeeded);
        Assert.Equal(0.125m, observation.EstimatedCostUsd);
        Assert.DoesNotContain("prompt", observation.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", observation.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecordsOnlyRequestsAcceptedByTheUsagePredicate()
    {
        var recorder = new CapturingRecorder();
        var handler = new AiUsageHttpMessageHandler(
            recorder,
            "Higgsfield",
            "models/video",
            "walkthrough-video",
            0.63m,
            request => request.Method == HttpMethod.Post
                && request.RequestUri?.AbsolutePath == "/models/video")
        {
            InnerHandler = new StubHandler(HttpStatusCode.OK),
        };
        using var client = new HttpClient(handler);

        using var pollResponse = await client.GetAsync("https://provider.example/models/video/status");
        using var generationResponse = await client.PostAsync(
            "https://provider.example/models/video",
            new StringContent("generation request"));

        pollResponse.EnsureSuccessStatusCode();
        generationResponse.EnsureSuccessStatusCode();
        var observation = Assert.Single(recorder.Observations);
        Assert.Equal("walkthrough-video", observation.Operation);
        Assert.Equal(0.63m, observation.EstimatedCostUsd);
    }

    private sealed class CapturingRecorder : IAiUsageRecorder
    {
        public List<AiCallObservation> Observations { get; } = [];

        public ValueTask RecordAsync(AiCallObservation observation, CancellationToken cancellationToken = default)
        {
            Observations.Add(observation);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode));
    }
}
