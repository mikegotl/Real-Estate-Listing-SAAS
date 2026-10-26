using System.Net;
using System.Net.Http.Json;
using ListingStudio.AI.Audio;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class ElevenLabsUsageCostEstimatorTests
{
    [Fact]
    public async Task UsesProviderCharacterCostHeaderWhenAvailable()
    {
        using var request = Request("This fallback text must not be used.");
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("character-cost", "1250");

        var cost = await ElevenLabsUsageCostEstimator.EstimateAsync(request, response, 0.08m);

        Assert.Equal(0.10m, cost);
    }

    [Fact]
    public async Task FallsBackToSubmittedTextLengthWhenHeaderIsMissing()
    {
        using var request = Request("Hello!");
        using var response = new HttpResponseMessage(HttpStatusCode.OK);

        var cost = await ElevenLabsUsageCostEstimator.EstimateAsync(request, response, 0.08m);

        Assert.Equal(0.00048m, cost);
    }

    [Fact]
    public async Task DoesNotAssignCostToFailedRequests()
    {
        using var request = Request("Hello!");
        using var response = new HttpResponseMessage(HttpStatusCode.BadRequest);
        response.Headers.Add("character-cost", "500");

        var cost = await ElevenLabsUsageCostEstimator.EstimateAsync(request, response, 0.08m);

        Assert.Equal(0, cost);
    }

    private static HttpRequestMessage Request(string text) => new(HttpMethod.Post, "https://api.elevenlabs.test")
    {
        Content = JsonContent.Create(new { text }),
    };
}
