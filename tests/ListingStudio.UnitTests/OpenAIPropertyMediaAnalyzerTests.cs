using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ListingStudio.AI.Configuration;
using ListingStudio.AI.Properties;
using ListingStudio.Application.Properties;
using ListingStudio.Domain.Properties;
using Microsoft.Extensions.Options;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class OpenAIPropertyMediaAnalyzerTests
{
    [Fact]
    public async Task AnalyzeSendsImageWithStrictSchemaAndParsesStructuredOutput()
    {
        const string responseJson = """
            {
              "output": [
                {
                  "type": "message",
                  "content": [
                    {
                      "type": "output_text",
                      "text": "{\"category\":\"FrontExterior\",\"roomType\":\"Exterior\",\"qualityScore\":91,\"heroScore\":96,\"isExterior\":true,\"isInterior\":false,\"containsPeople\":false,\"potentialProblems\":[\"Parked car\"],\"description\":\"Front exterior of a detached home.\",\"suggestedDisplayOrder\":0}"
                    }
                  ]
                }
              ]
            }
            """;
        var handler = new RecordingHandler(responseJson);
        using var httpClient = new HttpClient(handler);
        var analyzer = new OpenAIPropertyMediaAnalyzer(
            httpClient,
            Options.Create(new OpenAIOptions
            {
                ApiKey = "test-api-key",
                Model = "test-vision-model",
                ResponsesEndpoint = "https://api.openai.test/v1/responses",
            }));
        await using var content = new MemoryStream([1, 2, 3, 4]);

        var result = await analyzer.AnalyzeAsync(
            new PropertyMediaAnalysisInput(Guid.NewGuid(), "front.png", "image/png", content));

        Assert.Equal(PropertyMediaCategory.FrontExterior, result.Category);
        Assert.Equal(96, result.HeroScore);
        Assert.Equal(["Parked car"], result.PotentialProblems);
        Assert.Equal(new AuthenticationHeaderValue("Bearer", "test-api-key"), handler.Authorization);
        using var request = JsonDocument.Parse(handler.RequestBody!);
        Assert.False(request.RootElement.GetProperty("store").GetBoolean());
        Assert.True(request.RootElement.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean());
        var imageUrl = request.RootElement
            .GetProperty("input")[0]
            .GetProperty("content")[1]
            .GetProperty("image_url")
            .GetString();
        Assert.Equal("data:image/png;base64,AQIDBA==", imageUrl);
    }

    private sealed class RecordingHandler(string responseJson) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        public AuthenticationHeaderValue? Authorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization;
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }
}
