using Bunit;
using ListingStudio.Application.Campaigns;
using ListingStudio.Application.Videos;
using ListingStudio.Web.Components.Properties;
using MudBlazor.Services;
using Xunit;

namespace ListingStudio.UnitTests;

/// <summary>The admin view of a Kling 4 request names the photo in each image slot, for both request layouts.</summary>
public sealed class KlingRequestDetailsTests : IAsyncDisposable
{
    private static readonly Guid Portrait = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid LivingRoom = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid Kitchen = Guid.Parse("66666666-6666-6666-6666-666666666666");

    private readonly BunitContext context = new();

    public KlingRequestDetailsTests()
    {
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
    }

    public ValueTask DisposeAsync() => context.DisposeAsync();

    [Fact]
    public void FirstTourPhotoIsTheOpeningFrameAndTheHeadshotLeadsImageUrls()
    {
        var body = $$"""
            {"image_url":"https://cdn.test/living.jpg","image_urls":["https://cdn.test/agent.jpg","https://cdn.test/kitchen.jpg"],"prompt":"... {{NativePresenterVideoPolicy.FirstTourPhotoOpensMarker}}; tour keyframes 2 to 2 ...","duration":15}
            """;

        var labels = SlotLabels(body);

        Assert.Equal(["Living room", "Profile headshot", "Kitchen"], labels);
    }

    [Fact]
    public void OlderRequestsKeepTheHeadshotInImageUrl()
    {
        const string body = """
            {"image_url":"https://cdn.test/agent.jpg","image_urls":["https://cdn.test/living.jpg","https://cdn.test/kitchen.jpg"],"prompt":"The real-estate agent in the image_url photo is the on-camera presenter.","duration":15}
            """;

        var labels = SlotLabels(body);

        Assert.Equal(["Profile headshot", "Living room", "Kitchen"], labels);
    }

    private string[] SlotLabels(string body)
    {
        var cut = context.Render<KlingRequestDetails>(parameters => parameters
            .Add(component => component.BodyJson, body)
            .Add(component => component.AgentPortraitId, Portrait)
            .Add(component => component.Frames, new KlingNativePresenterPreviewFrame[]
            {
                new(1, LivingRoom, "Living room", null, null),
                new(2, Kitchen, "Kitchen", null, null),
            }));
        return cut.FindAll("img").Select(image => image.GetAttribute("alt") ?? string.Empty).ToArray();
    }
}
