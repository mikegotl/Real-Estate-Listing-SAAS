using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ListingStudio.AI.Configuration;
using ListingStudio.AI.Stories;
using ListingStudio.Application.Stories;
using ListingStudio.Domain.Properties;
using Microsoft.Extensions.Options;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class OpenAIPropertyStoryGeneratorTests
{
    [Fact]
    public async Task GenerateSendsGroundedInputsWithStrictSchemaAndParsesStructuredOutput()
    {
        const string responseJson = """
            {
              "output": [
                {
                  "type": "message",
                  "content": [
                    {
                      "type": "output_text",
                      "text": "{\"campaignTitle\":\"Main Street Welcome\",\"openingHook\":\"Come inside.\",\"propertyNarrative\":\"A comfortable home.\",\"highlights\":[\"Three bedrooms\"],\"voiceoverScript\":\"Welcome home.\",\"closingCta\":\"Contact Example Realty.\",\"socialCaptionLong\":\"Discover this home.\",\"socialCaptionShort\":\"Welcome home.\"}"
                    }
                  ]
                }
              ]
            }
            """;
        var handler = new RecordingHandler(responseJson);
        using var httpClient = new HttpClient(handler);
        var generator = new OpenAIPropertyStoryGenerator(
            httpClient,
            Options.Create(new OpenAIOptions
            {
                ApiKey = "test-api-key",
                Model = "test-text-model",
                ResponsesEndpoint = "https://api.openai.test/v1/responses",
            }));
        var request = new PropertyStoryGenerationRequest(
            new VerifiedPropertyData(
                "123 Main Street",
                null,
                "Raleigh",
                "NC",
                "27601",
                450_000m,
                3,
                2.5m,
                2_100,
                null,
                1998,
                PropertyType.SingleFamily,
                "A comfortable home.",
                ListingStatus.Active),
            [],
            new PropertyStoryBranding("Example Realty", null));

        var result = await generator.GenerateAsync(request);

        Assert.Equal("Main Street Welcome", result.CampaignTitle);
        Assert.Equal("property-story-v3", generator.GenerationVersion);
        Assert.Equal(new AuthenticationHeaderValue("Bearer", "test-api-key"), handler.Authorization);
        using var sent = JsonDocument.Parse(handler.RequestBody!);
        Assert.False(sent.RootElement.GetProperty("store").GetBoolean());
        Assert.True(sent.RootElement.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean());
        var input = sent.RootElement.GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString();
        Assert.Contains("verified_property_data", input, StringComparison.Ordinal);
        Assert.Contains("123 Main Street", input, StringComparison.Ordinal);
        Assert.Contains("media_observations", input, StringComparison.Ordinal);
        Assert.Contains("branding", input, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateRemovesRedundantParentheticalNeighborhoodCategories()
    {
        const string responseJson = """
            {
              "output": [
                {
                  "type": "message",
                  "content": [
                    {
                      "type": "output_text",
                      "text": "{\"campaignTitle\":\"Nearby conveniences\",\"openingHook\":\"Welcome.\",\"propertyNarrative\":\"Near Walker Elementary School (School).\",\"highlights\":[\"Publix Super Market (Grocery) is nearby\"],\"voiceoverScript\":\"Nearby places include Walker Elementary School (School), 0.54 miles away, and Publix Super Market (Grocery), 0.87 miles away.\",\"closingCta\":\"Contact Example Realty.\",\"socialCaptionLong\":\"Walker Elementary School (School) and Publix Super Market (Grocery) are nearby.\",\"socialCaptionShort\":\"Explore the area.\"}"
                    }
                  ]
                }
              ]
            }
            """;
        var handler = new RecordingHandler(responseJson);
        using var httpClient = new HttpClient(handler);
        var generator = new OpenAIPropertyStoryGenerator(
            httpClient,
            Options.Create(new OpenAIOptions
            {
                ApiKey = "test-api-key",
                Model = "test-text-model",
                ResponsesEndpoint = "https://api.openai.test/v1/responses",
            }));
        var request = new PropertyStoryGenerationRequest(
            new VerifiedPropertyData(
                "123 Main Street", null, "Raleigh", "NC", "27601", 450_000m, 3, 2.5m, 2_100,
                null, 1998, PropertyType.SingleFamily, "A comfortable home.", ListingStatus.Active),
            [],
            new PropertyStoryBranding("Example Realty", null),
            [
                new ApprovedNeighborhoodFact(
                    "School", "Walker Elementary School", "3101 Snow Hill Road", 0.54m,
                    "https://maps.google.test/school", DateTimeOffset.UtcNow),
                new ApprovedNeighborhoodFact(
                    "Grocery", "Publix Super Market", "443 County Road 419", 0.87m,
                    "https://maps.google.test/grocery", DateTimeOffset.UtcNow),
            ]);

        var result = await generator.GenerateAsync(request);

        var allText = string.Join('\n',
            new[]
            {
                result.PropertyNarrative,
                result.VoiceoverScript,
                result.SocialCaptionLong,
            }.Concat(result.Highlights));
        Assert.DoesNotContain("(School)", allText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("(Grocery)", allText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Walker Elementary School, 0.54 miles away", result.VoiceoverScript, StringComparison.Ordinal);
        Assert.Contains("Publix Super Market, 0.87 miles away", result.VoiceoverScript, StringComparison.Ordinal);
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
