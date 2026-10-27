using ListingStudio.Application.Properties;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Infrastructure.Campaigns;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class CampaignStillPostTests
{
    [Fact]
    public void ComposerCreatesExactFeedCropWithoutChangingTheSource()
    {
        var sourcePixels = Enumerable.Range(0, 8 * 4 * 3).Select(value => (byte)value).ToArray();
        var source = new PhotoPixels(8, 4, sourcePixels.ToArray());

        var result = CampaignStillPostComposer.Compose(source, 4, 5);

        Assert.Equal(4, result.Width);
        Assert.Equal(5, result.Height);
        Assert.Equal(4 * 5 * 3, result.Rgb.Length);
        Assert.Equal(sourcePixels, source.Rgb);
        Assert.Equal(new byte[] { 205, 158, 76 }, result.Rgb[^3..]);
    }

    [Fact]
    public void RenderedPostKeepsItsCampaignAndCaptionMetadata()
    {
        var jobId = Guid.NewGuid();
        var organizationId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        var post = CampaignStillPost.Create(
            jobId,
            organizationId,
            propertyId,
            CampaignStillPostKind.FacebookPortrait,
            1080,
            1350,
            "Verified property caption",
            now);

        post.MarkRendered("campaign/post.jpg", "image/jpeg", 1_024, now.AddSeconds(1));

        Assert.Equal(CampaignStillPostStatus.Rendered, post.Status);
        Assert.Equal("Verified property caption", post.Caption);
        Assert.Equal(1080, post.Width);
        Assert.Equal(1350, post.Height);
        Assert.Equal("image/jpeg", post.ContentType);
    }
}
