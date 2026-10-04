using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ListingStudio.AI.Configuration;
using ListingStudio.AI.Videos;
using ListingStudio.Application.Stories;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Properties;
using ListingStudio.Domain.Videos;
using Microsoft.Extensions.Options;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class OpenAIVideoDirectorTests
{
    [Fact]
    public async Task DirectSendsEditorialInputsWithStrictSchemaAndParsesStructuredOutput()
    {
        var request = CreateRequest();
        var viewport = new NormalizedRect(0, 0, 1, 1);
        var expected = new DirectedEditorialPlan(
            new AudioPlan(
                [new NarrationSegment("narration-1", 500, 3_000, "Welcome home.", "story.voiceover")],
                new MusicPlan(null, MusicMood.None, 0, 0, 0, 0, 0, 0)),
            [
                new VideoScene(
                    1,
                    0,
                    60_000,
                    new VisualSource(VisualSourceKind.PropertyMedia, request.Media[0].MediaId, null, null, null),
                    new TransitionPlan(TransitionKind.Cut, 0),
                    new MotionPlan(MotionKind.None, viewport, viewport, MotionEasing.Linear),
                    [
                        new TextOverlay(
                            "cta", "Contact us.", "story.closingCta", 55_000, 4_000,
                            OverlayAnchor.BottomCenter, new NormalizedRect(0.15m, 0.75m, 0.7m, 0.1m),
                            TextOverlayStyle.ClosingCta),
                    ],
                    [],
                    ["narration-1"]),
            ]);
        var outputText = JsonSerializer.Serialize(expected, VideoSpecificationJson.Options);
        var responseJson = JsonSerializer.Serialize(new
        {
            output = new[]
            {
                new
                {
                    type = "message",
                    content = new[] { new { type = "output_text", text = outputText } },
                },
            },
        });
        var handler = new RecordingHandler(responseJson);
        using var httpClient = new HttpClient(handler);
        var director = new OpenAIVideoDirector(
            httpClient,
            Options.Create(new OpenAIOptions
            {
                ApiKey = "test-api-key",
                Model = "test-model",
                ResponsesEndpoint = "https://api.openai.test/v1/responses",
            }));

        var result = await director.DirectAsync(request);

        Assert.Equal("openai-video-director-v1", director.DirectorVersion);
        Assert.Single(result.Scenes);
        Assert.Equal(request.Media[0].MediaId, result.Scenes[0].VisualSource.PropertyMediaId);
        Assert.Equal(new AuthenticationHeaderValue("Bearer", "test-api-key"), handler.Authorization);
        using var sent = JsonDocument.Parse(handler.RequestBody!);
        Assert.False(sent.RootElement.GetProperty("store").GetBoolean());
        var format = sent.RootElement.GetProperty("text").GetProperty("format");
        Assert.True(format.GetProperty("strict").GetBoolean());
        var schema = format.GetProperty("schema");
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        foreach (var definition in schema.GetProperty("$defs").EnumerateObject())
        {
            if (definition.Value.TryGetProperty("type", out var type) && type.GetString() == "object")
            {
                Assert.False(definition.Value.GetProperty("additionalProperties").GetBoolean());
            }
        }
        var input = sent.RootElement.GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString();
        Assert.Contains("verified_property_data", input, StringComparison.Ordinal);
        Assert.Contains(request.Media[0].MediaId.ToString(), input, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fact_bindings", input, StringComparison.Ordinal);
        Assert.Contains("brand", input, StringComparison.Ordinal);
        Assert.DoesNotContain("safeZone", input, StringComparison.Ordinal);
        Assert.DoesNotContain("videoCodec", input, StringComparison.Ordinal);
    }

    private static VideoDirectionRequest CreateRequest()
    {
        var propertyId = Guid.NewGuid();
        var mediaId = Guid.NewGuid();
        return new VideoDirectionRequest(
            propertyId,
            new VideoPropertyStoryInput(
                Guid.NewGuid(),
                1,
                new PropertyStoryContentSnapshot("Welcome", "Come inside.", "A home.", [], "Welcome home.", "Contact us.")),
            new VerifiedPropertyData(
                "123 Main Street", null, "Raleigh", "NC", "27601", 450_000m, 3, 2.5m, 2_100, null,
                1998, PropertyType.SingleFamily, "A home.", ListingStatus.Active),
            [
                new VideoMediaInput(
                    mediaId, 1_600, 900,
                    new PropertyMediaObservation(
                        mediaId, 0, PropertyMediaCategory.FrontExterior, "Exterior", 90, 95,
                        true, false, false, [], "Front exterior.", 0)),
            ],
            RequestedDuration.Hero60,
            VideoAspectRatio.Landscape16By9,
            new VideoOutputProfile(1920, 1080, 30, "h264", "aac", "yuv420p", 48_000, 2),
            new NormalizedRect(0.05m, 0.05m, 0.9m, 0.9m),
            [
                new FactBinding("story.voiceover", "Welcome home.", FactSource.PropertyStory, "VoiceoverScript"),
                new FactBinding("story.closingCta", "Contact us.", FactSource.PropertyStory, "ClosingCta"),
            ],
            new VideoBrandPlan(null, null, null, null, null, null, "#17324D", "#F4F0E8"),
            new GroundedText("Contact us.", "story.closingCta"),
            new HashSet<Guid>(),
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal));
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
