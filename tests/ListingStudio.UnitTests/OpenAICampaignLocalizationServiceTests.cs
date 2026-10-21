using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ListingStudio.AI.Campaigns;
using ListingStudio.AI.Configuration;
using ListingStudio.Application.Campaigns;
using ListingStudio.Domain.Campaigns;
using Microsoft.Extensions.Options;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class OpenAICampaignLocalizationServiceTests
{
    [Fact]
    public async Task EnglishDefaultsReuseGroundedCopyWithoutCallingProvider()
    {
        var handler = new RecordingHandler("provider should not be called");
        using var client = new HttpClient(handler);
        var service = CreateService(client);

        var result = await service.LocalizeAsync(new CampaignLocalizationRequest(
            CampaignNarrationPreferences.Default,
            [new CampaignLocalizationSegment("scene-1", "Welcome to 123 Main Street.")]));

        Assert.Equal("Welcome to 123 Main Street.", Assert.Single(result.SpokenSegments).Text);
        Assert.Equal("Welcome to 123 Main Street.", Assert.Single(result.CaptionSegments).Text);
        Assert.Null(handler.RequestBody);
    }

    [Fact]
    public async Task TranslatesSpokenAndCaptionCopySeparatelyWithStrictGrounding()
    {
        var output = JsonSerializer.Serialize(new CampaignLocalizedCopy(
            [new CampaignLocalizedSegment("scene-1", "Bienvenido a 123 Main Street.")],
            [new CampaignLocalizedSegment("scene-1", "Bienvenue au 123 Main Street.")]));
        var response = JsonSerializer.Serialize(new
        {
            output = new[]
            {
                new { content = new[] { new { type = "output_text", text = output } } },
            },
        });
        var handler = new RecordingHandler(response);
        using var client = new HttpClient(handler);
        var service = CreateService(client);

        var result = await service.LocalizeAsync(new CampaignLocalizationRequest(
            new CampaignNarrationPreferences(
                CampaignNarrationRegion.Mexico,
                CampaignLanguage.Spanish,
                CampaignLanguage.French,
                CampaignVoiceAccent.LatinAmerican),
            [new CampaignLocalizationSegment("scene-1", "Welcome to 123 Main Street.")]));

        Assert.Equal("Bienvenido a 123 Main Street.", Assert.Single(result.SpokenSegments).Text);
        Assert.Equal("Bienvenue au 123 Main Street.", Assert.Single(result.CaptionSegments).Text);
        Assert.Equal(new AuthenticationHeaderValue("Bearer", "test-key"), handler.Authorization);
        using var sent = JsonDocument.Parse(handler.RequestBody!);
        Assert.True(sent.RootElement.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean());
        var input = sent.RootElement.GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString();
        Assert.Contains("Mexico", input, StringComparison.Ordinal);
        Assert.Contains("Spanish", input, StringComparison.Ordinal);
        Assert.Contains("French", input, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsTranslationThatChangesProtectedNumbers()
    {
        var output = JsonSerializer.Serialize(new CampaignLocalizedCopy(
            [new CampaignLocalizedSegment("scene-1", "Precio 999.")],
            [new CampaignLocalizedSegment("scene-1", "Precio 999.")]));
        var response = JsonSerializer.Serialize(new
        {
            output = new[] { new { content = new[] { new { type = "output_text", text = output } } } },
        });
        using var client = new HttpClient(new RecordingHandler(response));
        var service = CreateService(client);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => service.LocalizeAsync(
            new CampaignLocalizationRequest(
                new CampaignNarrationPreferences(
                    CampaignNarrationRegion.Mexico,
                    CampaignLanguage.Spanish,
                    CampaignLanguage.Spanish,
                    CampaignVoiceAccent.LatinAmerican),
                [new CampaignLocalizationSegment("scene-1", "Price 450000.")])));

        Assert.Contains("protected number", exception.Message, StringComparison.Ordinal);
    }

    private static OpenAICampaignLocalizationService CreateService(HttpClient client) => new(
        client,
        Options.Create(new OpenAIOptions
        {
            ApiKey = "test-key",
            Model = "test-model",
            ResponsesEndpoint = "https://api.openai.test/v1/responses",
        }));

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
