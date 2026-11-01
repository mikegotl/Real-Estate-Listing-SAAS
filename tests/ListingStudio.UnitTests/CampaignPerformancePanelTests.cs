using Bunit;
using ListingStudio.Application.Campaigns;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Web.Components.Properties;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Xunit;

namespace ListingStudio.UnitTests;

/// <summary>Prompt 10: the Campaign tab's performance panel and the shared comparison table.</summary>
public sealed class CampaignPerformancePanelTests : IAsyncDisposable
{
    private static readonly Guid JobId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid WowDeliverable = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid PricePost = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private readonly BunitContext context = new();
    private readonly FakePerformance service = new();

    public CampaignPerformancePanelTests()
    {
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton<IVerticalPerformanceService>(service);
        context.Services.AddSingleton(TimeProvider.System);
        context.AddAuthorization().SetAuthorized("agent").SetClaims(
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "agent-1"));
    }

    public ValueTask DisposeAsync() => context.DisposeAsync();

    [Fact]
    public void TheBusinessWinnerStaysVisibleNextToTheReachWinner()
    {
        var cut = Render();
        var wow = cut.Find("[data-testid=performance-row-WowFeature-a]");
        var price = cut.Find("[data-testid=performance-row-PriceFirst-a]");
        Assert.Contains("Most reach", wow.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Most leads", wow.TextContent, StringComparison.Ordinal);
        Assert.Contains("Most leads", price.TextContent, StringComparison.Ordinal);
        Assert.Contains("3%", price.TextContent, StringComparison.Ordinal);
        Assert.Contains("6", price.TextContent, StringComparison.Ordinal);
        Assert.Contains("No numbers yet", cut.Find("[data-testid=performance-row-StoryReveal-a]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Reach and engagement", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Business results", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(VerticalPerformanceCalculator.CalculationVersion, cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void PostsAndNumbersAreEnteredByHand()
    {
        var cut = Render();
        cut.Find("[data-testid=post-video]").Change(WowDeliverable.ToString());
        cut.Find("[data-testid=post-platform]").Change(nameof(DistributionPlatform.InstagramReels));
        cut.Find("[data-testid=post-url]").Change("https://www.instagram.com/reel/abc");
        cut.Find("form[aria-labelledby=add-post-heading]").Submit();
        Assert.Equal((WowDeliverable, DistributionPlatform.InstagramReels, "https://www.instagram.com/reel/abc"), service.Posts.Single());
        Assert.Contains("Post added", cut.Find("[data-testid=performance-message]").TextContent, StringComparison.Ordinal);

        cut.Find("[data-testid=numbers-post]").Change(PricePost.ToString());
        cut.Find("[data-testid=numbers-views]").Change("2000");
        cut.Find("[data-testid=numbers-likes]").Change("60");
        cut.Find("[data-testid=numbers-leads]").Change("6");
        cut.Find("[data-testid=numbers-completion]").Change("50");
        cut.Find("form[aria-labelledby=add-numbers-heading]").Submit();
        var (postId, values) = service.Snapshots.Single();
        Assert.Equal(PricePost, postId);
        Assert.Equal(new CampaignMetricValues(2_000, 60, 0, 0, 0, null, 0.5m, 0, 6, 0), values);

        service.Fail = new ArgumentException("Likes, comments, shares, saves and clicks can't be more than views.");
        cut.Find("form[aria-labelledby=add-numbers-heading]").Submit();
        Assert.Contains("can't be more than views", cut.Find("[data-testid=performance-message]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadOnlyCampaignsShowNumbersWithoutForms()
    {
        var cut = Render(readOnly: true);
        Assert.NotEmpty(cut.FindAll("[data-testid=performance-table]"));
        Assert.Empty(cut.FindAll("form"));
    }

    [Fact]
    public void SwitchedOffWorkspacesSeeNothing()
    {
        service.Enabled = false;
        var cut = Render();
        Assert.Empty(cut.FindAll("[data-testid=performance-panel]"));
    }

    private IRenderedComponent<CampaignPerformancePanel> Render(bool readOnly = false) =>
        context.Render<CampaignPerformancePanel>(parameters => parameters
            .Add(component => component.JobId, JobId)
            .Add(component => component.IsReadOnly, readOnly));

    private static VerticalPerformanceView View()
    {
        var now = DateTimeOffset.UnixEpoch.AddYears(56);
        VerticalPerformancePost Post(Guid id, CampaignMetricValues values) =>
            new(id, DistributionPlatform.TikTok, "https://www.tiktok.com/@a/video/1", now, 1, now, values);
        return new VerticalPerformanceView("This campaign", Guid.NewGuid(), JobId, null, VerticalPerformanceCalculator.CalculationVersion,
            VerticalPerformanceCalculator.Compare(
            [
                (Guid.NewGuid(), WowDeliverable, VerticalCreativeFormat.WowFeature, "a", VerticalCampaignPurpose.Reach,
                    [Post(Guid.NewGuid(), new CampaignMetricValues(10_000, 900, 50, 70, 40, 8m, 0.24m, 100, 1, 0))]),
                (Guid.NewGuid(), Guid.NewGuid(), VerticalCreativeFormat.PriceFirst, "a", VerticalCampaignPurpose.Consideration,
                    [Post(PricePost, new CampaignMetricValues(2_000, 60, 5, 4, 20, 12m, 0.5m, 100, 6, 2))]),
                (Guid.NewGuid(), Guid.NewGuid(), VerticalCreativeFormat.StoryReveal, "a", VerticalCampaignPurpose.Reach, []),
            ]));
    }

    private sealed class FakePerformance : IVerticalPerformanceService
    {
        public bool Enabled { get; set; } = true;

        public Exception? Fail { get; set; }

        public List<(Guid, DistributionPlatform, string)> Posts { get; } = [];

        public List<(Guid, CampaignMetricValues)> Snapshots { get; } = [];

        public Task<bool> IsEnabledAsync(string userId, CancellationToken cancellationToken = default) => Task.FromResult(Enabled);

        public Task<VerticalPerformanceView?> GetCampaignAsync(string userId, Guid jobId, CancellationToken cancellationToken = default) =>
            Enabled ? Task.FromResult<VerticalPerformanceView?>(View()) : throw new PerformanceTrackingDisabledException();

        public Task<Guid> AddDistributionAsync(string userId, Guid deliverableId, DistributionPlatform platform, string postUrl,
            DateTimeOffset publishedAtUtc, CancellationToken cancellationToken = default)
        {
            Posts.Add((deliverableId, platform, postUrl));
            return Task.FromResult(Guid.NewGuid());
        }

        public Task AddSnapshotAsync(string userId, Guid distributionId, CampaignMetricValues values, DateTimeOffset capturedAtUtc,
            CancellationToken cancellationToken = default)
        {
            if (Fail is not null)
            {
                throw Fail;
            }

            Snapshots.Add((distributionId, values));
            return Task.CompletedTask;
        }

        public Task<MetricImportResult> ImportCsvAsync(string userId, Stream csv, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MetricImportResult(0, ["Line 2: views must be a whole number."]));
    }
}
