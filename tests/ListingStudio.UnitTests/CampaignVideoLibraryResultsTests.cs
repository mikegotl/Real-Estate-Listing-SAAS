using Bunit;
using Bunit.TestDoubles;
using ListingStudio.Application.Campaigns;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Web.Components.Properties;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Xunit;

namespace ListingStudio.UnitTests;

/// <summary>The library result cards: one per video, reviewed and downloaded by deliverable id and version.</summary>
public sealed class CampaignVideoLibraryResultsTests : IAsyncDisposable
{
    private readonly BunitContext context = new();
    private readonly FakeLibraryService service = new();

    public CampaignVideoLibraryResultsTests()
    {
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton<ICampaignVideoLibraryService>(service);
        context.AddAuthorization().SetAuthorized("agent").SetClaims(
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "agent-1"));
        context.ComponentFactories.AddStub<CampaignVideoChatPanel>();
    }

    public ValueTask DisposeAsync() => context.DisposeAsync();

    [Fact]
    public void ShowsOneCardPerVideoWithItsStatusReviewAndVersionedLinks()
    {
        var cut = Render();

        Assert.Equal(3, cut.FindAll("[data-testid^=library-card-]").Count);
        var wow = cut.Find("[data-testid=library-card-WowFeature]");
        Assert.Contains("Reach new viewers", wow.TextContent, StringComparison.Ordinal);
        Assert.Contains("wow-feature-v1", wow.TextContent, StringComparison.Ordinal);
        Assert.Contains("Look at this kitchen.", wow.TextContent, StringComparison.Ordinal);
        Assert.Contains("Score 89/100", wow.TextContent, StringComparison.Ordinal);
        Assert.Contains("Technical check passed", cut.Find("[data-testid=technical-WowFeature]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Approved", cut.Find("[data-testid=decision-WowFeature]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Not reviewed", cut.Find("[data-testid=decision-PriceFirst]").TextContent, StringComparison.Ordinal);
        Assert.Contains($"/campaigns/videos/{FakeLibraryService.Wow}?v=1", wow.InnerHtml, StringComparison.Ordinal);
        Assert.Contains($"/campaigns/videos/{FakeLibraryService.Price}?v=2&amp;download=true", cut.Markup, StringComparison.Ordinal);
        Assert.Contains($"/campaigns/videos/{FakeLibraryService.Price}/caption", cut.Markup, StringComparison.Ordinal);
        Assert.Contains($"/campaigns/{FakeLibraryService.Job}/library.zip", cut.Find("[data-testid=download-library]").GetAttribute("href"),
            StringComparison.Ordinal);
        Assert.Contains("2 of 3 videos ready", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("TikTok, Instagram Reels or YouTube Shorts", cut.Markup, StringComparison.Ordinal);

        var failed = cut.Find("[data-testid=library-card-HostedPov]");
        Assert.Contains("Couldn't be made", failed.TextContent, StringComparison.Ordinal);
        Assert.Contains("The voiceover runs longer than the video.", failed.TextContent, StringComparison.Ordinal);
        Assert.Empty(failed.QuerySelectorAll("video"));
        Assert.Empty(cut.FindAll("[data-testid=decision-HostedPov]"));
        Assert.Equal(2, cut.FindComponents<Stub<CampaignVideoChatPanel>>().Count);
    }

    [Fact]
    public void ApprovingSendsThatVideosIdAndCurrentVersionOnly()
    {
        var cut = Render();

        cut.Find("[data-testid=approve-PriceFirst]").Click();

        var decision = Assert.Single(service.Decisions);
        Assert.Equal((FakeLibraryService.Price, 2, CampaignVideoReviewDecision.Approved), (decision.Id, decision.Version, decision.Decision));
        Assert.Contains("Price first approved.", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadOnlyPropertiesShowVideosWithoutReviewOrChat()
    {
        var cut = Render(readOnly: true);

        Assert.Empty(cut.FindAll("[data-testid^=approve-]"));
        Assert.Empty(cut.FindComponents<Stub<CampaignVideoChatPanel>>());
        Assert.Equal(2, cut.FindAll("video").Count);
    }

    [Fact]
    public void LegacyCampaignsRenderNothing()
    {
        service.Empty = true;
        var cut = Render();
        Assert.Empty(cut.FindAll("[data-testid=video-library]"));
    }

    [Fact]
    public void TheGuideLinkShowsOnlyWhileTheLibraryIsSwitchedOn()
    {
        Assert.Empty(Render().FindAll("[data-testid=guide-link-library-gallery]"));
        var link = Render(guide: true).Find("[data-testid=guide-link-library-gallery]");
        Assert.Equal("/go/article/one-listing-four-tiktoks?from=library-gallery", link.GetAttribute("href"));
    }

    private IRenderedComponent<CampaignVideoLibraryResults> Render(bool readOnly = false, bool guide = false) =>
        context.Render<CampaignVideoLibraryResults>(parameters => parameters
            .Add(component => component.JobId, FakeLibraryService.Job)
            .Add(component => component.Stamp, DateTimeOffset.UnixEpoch)
            .Add(component => component.IsReadOnly, readOnly)
            .Add(component => component.ShowGuideLink, guide));

    private sealed class FakeLibraryService : ICampaignVideoLibraryService
    {
        public static readonly Guid Job = Guid.Parse("11111111-1111-1111-1111-111111111111");
        public static readonly Guid Wow = Guid.Parse("22222222-2222-2222-2222-222222222222");
        public static readonly Guid Price = Guid.Parse("33333333-3333-3333-3333-333333333333");

        public bool Empty { get; set; }

        public List<(Guid Id, int Version, CampaignVideoReviewDecision Decision)> Decisions { get; } = [];

        public Task<CampaignVideoLibraryView?> GetAsync(string userId, Guid jobId, CancellationToken cancellationToken = default)
        {
            Assert.Equal("agent-1", userId);
            if (Empty)
            {
                return Task.FromResult<CampaignVideoLibraryView?>(null);
            }

            var captions = new VerticalVariantCaptions("WowFeature", "Short caption", "Long caption", "Look at this kitchen.", "Call to book", []);
            return Task.FromResult<CampaignVideoLibraryView?>(new CampaignVideoLibraryView(
                Job, Guid.NewGuid(), CampaignVideoLibraryStatus.PartiallyCompleted, VerticalVideoSelection.RecommendedThree,
                VerticalCampaignGoal.NewListing, "vertical-rules-v1",
                [
                    new CampaignLibraryVideoView(Guid.NewGuid(), Wow, VerticalCreativeFormat.WowFeature, VerticalCampaignPurpose.Reach, 1,
                        89, VerticalRecommendationStrength.Strong, ["A standout kitchen photo can open the video."],
                        "Look at this kitchen.", "Call to book", 15, "wow-feature-v1", CampaignVideoVariantStatus.Rendered,
                        CampaignVideoTechnicalStatus.Passed, null, 1,
                        new CampaignVideoReviewState(1, CampaignVideoReviewDecision.Approved, null, []), captions),
                    new CampaignLibraryVideoView(Guid.NewGuid(), Price, VerticalCreativeFormat.PriceFirst, VerticalCampaignPurpose.Consideration, 2,
                        75, VerticalRecommendationStrength.Strong, ["The listing price is entered."],
                        "$450,000 in Raleigh, NC", "Call to book", 30, "price-first-v1", CampaignVideoVariantStatus.Rendered,
                        CampaignVideoTechnicalStatus.Passed, null, 2,
                        new CampaignVideoReviewState(2, null, null,
                            [new CampaignReviewEntry(Guid.NewGuid(), 1, CampaignVideoReviewDecision.ChangesRequested, "Faster", DateTimeOffset.UnixEpoch)]),
                        captions with { Format = "PriceFirst" }),
                    new CampaignLibraryVideoView(Guid.NewGuid(), Guid.NewGuid(), VerticalCreativeFormat.HostedPov, VerticalCampaignPurpose.LeadGeneration, 3,
                        60, VerticalRecommendationStrength.Good, [], "Welcome", "Call to book", 30, "hosted-pov-v1",
                        CampaignVideoVariantStatus.Failed, CampaignVideoTechnicalStatus.Failed, "The voiceover runs longer than the video.", 1,
                        CampaignVideoReviewState.None(1), null),
                ]));
        }

        public Task<CampaignVideoReviewState?> DecideAsync(string userId, Guid deliverableId, int expectedVersion,
            CampaignVideoReviewDecision decision, string? feedback, CancellationToken cancellationToken = default)
        {
            Decisions.Add((deliverableId, expectedVersion, decision));
            return Task.FromResult<CampaignVideoReviewState?>(new CampaignVideoReviewState(expectedVersion, decision, feedback, []));
        }

        public Task<CampaignDownload?> OpenVideoAsync(string userId, Guid deliverableId, int? version, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CampaignDownload?> OpenCaptionAsync(string userId, Guid deliverableId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CampaignLibraryArchive?> OpenArchiveAsync(string userId, Guid jobId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
