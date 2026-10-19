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
        Assert.Equal("property-story-v5", generator.GenerationVersion);
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
                      "text": "{\"campaignTitle\":\"Nearby conveniences\",\"openingHook\":\"Welcome.\",\"propertyNarrative\":\"Near Walker Elementary School (School).\",\"highlights\":[\"Publix Super Market (Grocery) is nearby\"],\"voiceoverScript\":\"Nearby places include Walker Elementary School (School), less than a mile away, and Publix Super Market (Grocery), about 2 miles away.\",\"closingCta\":\"Contact Example Realty.\",\"socialCaptionLong\":\"Walker Elementary School (School) and Publix Super Market (Grocery) are nearby.\",\"socialCaptionShort\":\"Explore the area.\"}"
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
                    "Grocery", "Publix Super Market", "443 County Road 419", 2.3m,
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
        Assert.Contains("Walker Elementary School, less than a mile away", result.VoiceoverScript, StringComparison.Ordinal);
        Assert.Contains("Publix Super Market, about 2 miles away", result.VoiceoverScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateSendsWholeMileNeighborhoodDistancesInsteadOfDecimals()
    {
        const string responseJson = """
            {
              "output": [
                {
                  "type": "message",
                  "content": [
                    {
                      "type": "output_text",
                      "text": "{\"campaignTitle\":\"Nearby\",\"openingHook\":\"Welcome.\",\"propertyNarrative\":\"Nearby places.\",\"highlights\":[\"Nearby places\"],\"voiceoverScript\":\"Nearby places.\",\"closingCta\":\"Contact Example Realty.\",\"socialCaptionLong\":\"Nearby places.\",\"socialCaptionShort\":\"Nearby.\"}"
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
                    "Park", "Lake Park", "1 Lake Drive", 0.54m,
                    "https://maps.google.test/park", DateTimeOffset.UtcNow),
                new ApprovedNeighborhoodFact(
                    "Grocery", "Publix Super Market", "443 County Road 419", 2.3m,
                    "https://maps.google.test/grocery", DateTimeOffset.UtcNow),
            ]);

        await generator.GenerateAsync(request);

        using var sent = JsonDocument.Parse(handler.RequestBody!);
        var input = sent.RootElement.GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString();
        Assert.Contains("\"approximateDistance\":\"less than a mile away\"", input, StringComparison.Ordinal);
        Assert.Contains("\"approximateDistance\":\"about 2 miles away\"", input, StringComparison.Ordinal);
        Assert.DoesNotContain("0.54", input, StringComparison.Ordinal);
        Assert.DoesNotContain("2.3", input, StringComparison.Ordinal);
        Assert.DoesNotContain("distanceMiles", input, StringComparison.Ordinal);
        var instructions = sent.RootElement.GetProperty("instructions").GetString();
        Assert.Contains("Never state a neighborhood distance as a decimal", instructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateSendsTheWalkthroughAndAsksForBroadRoomImpressionsInStopOrder()
    {
        const string responseJson = """
            {
              "output": [
                {
                  "type": "message",
                  "content": [
                    {
                      "type": "output_text",
                      "text": "{\"campaignTitle\":\"Walk\",\"openingHook\":\"Welcome.\",\"propertyNarrative\":\"A home.\",\"highlights\":[\"Bright kitchen\"],\"voiceoverScript\":\"Step inside.\",\"closingCta\":\"Contact Example Realty.\",\"socialCaptionLong\":\"A home.\",\"socialCaptionShort\":\"Home.\"}"
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
            Walkthrough:
            [
                new WalkthroughStoryStop(1, "Entry / foyer", "Main level", null, []),
                new WalkthroughStoryStop(
                    2,
                    "Kitchen",
                    "Main level",
                    "turn left",
                    [new WalkthroughStoryShot(Guid.NewGuid(), "Kitchen", "Large, bright kitchen with a big island.")]),
            ]);

        await generator.GenerateAsync(request);

        using var sent = JsonDocument.Parse(handler.RequestBody!);
        var input = sent.RootElement.GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("\"walkthrough\":[", input, StringComparison.Ordinal);
        Assert.True(
            input.IndexOf("Entry / foyer", StringComparison.Ordinal) < input.IndexOf("\"stop\":\"Kitchen\"", StringComparison.Ordinal));
        Assert.Contains("\"wayFromPreviousStop\":\"turn left\"", input, StringComparison.Ordinal);
        Assert.Contains("Large, bright kitchen with a big island.", input, StringComparison.Ordinal);
        var instructions = sent.RootElement.GetProperty("instructions").GetString();
        Assert.Contains("exactly that stop order", instructions, StringComparison.Ordinal);
        Assert.Contains("Never count things", instructions, StringComparison.Ordinal);
        Assert.Contains("leave it out rather than guess", instructions, StringComparison.Ordinal);
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
