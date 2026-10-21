using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ListingStudio.AI.Campaigns;
using ListingStudio.AI.Configuration;
using ListingStudio.Application.Campaigns;
using Microsoft.Extensions.Options;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class OpenAIAgentPortraitEnhancerTests
{
    [Fact]
    public async Task SendsIdentityPreservingEditAndReturnsTheImage()
    {
        var expected = new byte[] { 1, 2, 3, 4 };
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"{{\"data\":[{{\"b64_json\":\"{Convert.ToBase64String(expected)}\"}}]}}"),
        });
        var provider = Create(handler, enabled: true);

        var result = await provider.EnhanceAsync(new AgentPortraitEnhancementRequest(
            [9, 8, 7], "image/jpeg", 100, 120));

        Assert.Equal(expected, result);
        Assert.Equal("Bearer", handler.Authorization?.Scheme);
        Assert.Equal("test-key", handler.Authorization?.Parameter);
        Assert.Contains("preserve the person's identity exactly", handler.RequestBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("input_fidelity", handler.RequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public void RemainsUnavailableUntilExplicitlyEnabled()
    {
        Assert.False(Create(new CaptureHandler(new HttpResponseMessage(HttpStatusCode.OK)), enabled: false).IsEnabled);
    }

    private static OpenAIAgentPortraitEnhancer Create(HttpMessageHandler handler, bool enabled) => new(
        new HttpClient(handler),
        Options.Create(new OpenAIOptions { ApiKey = "test-key" }),
        Options.Create(new PhotoFixOptions
        {
            AgentPortraitEnhancementEnabled = enabled,
            ImageModel = "gpt-image-1",
            ImageEditsEndpoint = "https://api.openai.test/v1/images/edits",
            InputFidelity = "high",
        }));

    private sealed class CaptureHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public AuthenticationHeaderValue? Authorization { get; private set; }

        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization;
            RequestBody = Encoding.UTF8.GetString(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            return response;
        }
    }
}
