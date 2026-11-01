using Bunit;
using Bunit.TestDoubles;
using ListingStudio.Application.Campaigns;
using ListingStudio.Domain.Campaigns;
using ListingStudio.TestSupport;
using ListingStudio.Web.Components.Administration;
using ListingStudio.Web.Components.Properties;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Xunit;

namespace ListingStudio.UnitTests;

/// <summary>The admin Creative Format Testing tab: grounded preview, estimate, start, and per-deliverable review.</summary>
public sealed class AdminCreativeTestingTabTests : IAsyncDisposable
{
    private static readonly Guid PropertyId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid DeliverableA = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private readonly BunitContext context = new();
    private readonly FakeCreativeTests creative = new();
    private readonly FakeReviews reviews = new();
    private readonly FakeEvaluation evaluation = new();

    public AdminCreativeTestingTabTests()
    {
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton<IVerticalCreativeTestService>(creative);
        context.Services.AddSingleton<ICampaignReviewService>(reviews);
        context.Services.AddSingleton<ITikTokTestSuiteService>(new FakeProperties());
        context.Services.AddSingleton<IVerticalRuleEvaluationService>(evaluation);
        context.AddAuthorization().SetAuthorized("admin").SetClaims(
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "admin-1"));
        context.ComponentFactories.AddStub<CampaignVideoChatPanel>();
        // Static render: no polling loop starts.
        context.Renderer.SetRendererInfo(new Microsoft.AspNetCore.Components.RendererInfo("Static", false));
    }

    public ValueTask DisposeAsync() => context.DisposeAsync();

    [Fact]
    public void AnalyzingShowsGroundedScoresAndTheEstimateBeforeStarting()
    {
        var cut = context.Render<AdminCreativeTestingTab>();
        Assert.Contains("No creative tests yet", cut.Markup, StringComparison.Ordinal);
        Assert.Equal("/go/article/one-listing-four-tiktoks?from=admin-creative",
            cut.Find("[data-testid=guide-link-admin-creative]").GetAttribute("href"));
        cut.Find("[data-testid=creative-property]").Change(PropertyId.ToString());
        cut.Find("[data-testid=creative-analyze]").Click();

        Assert.Equal(VerticalFormatRules.Formats.Count, cut.FindAll("[data-testid^=creative-option-]").Count);
        var price = cut.Find("[data-testid=creative-option-PriceFirst]");
        Assert.Contains("Hook A", price.TextContent, StringComparison.Ordinal);
        Assert.Contains("What $450,000 gets you in Raleigh, NC", price.TextContent, StringComparison.Ordinal);
        Assert.Contains("price-first-v1", price.TextContent, StringComparison.Ordinal);
        Assert.Contains("This starts 3 test campaigns", cut.Find("[data-testid=creative-estimate]").TextContent, StringComparison.Ordinal);
        Assert.Contains("generation credits", cut.Find("[data-testid=creative-estimate]").TextContent, StringComparison.Ordinal);

        cut.Find("[data-testid=creative-mode-HookAb]").Change(true);
        Assert.Contains("Choose a format", cut.Find("[data-testid=creative-estimate]").TextContent, StringComparison.Ordinal);
        Assert.True(cut.Find("[data-testid=creative-start]").HasAttribute("disabled"));
        cut.Find("[data-testid=creative-format]").Change(nameof(VerticalCreativeFormat.PriceFirst));
        Assert.Contains("This starts 2 test campaigns", cut.Find("[data-testid=creative-estimate]").TextContent, StringComparison.Ordinal);
        Assert.Equal("F · Neon night", cut.Find("[data-testid=caption-picker-current]").TextContent.Trim());
        cut.Find("[data-testid=caption-picker-toggle]").Click();
        cut.Find("[data-testid=caption-style-E]").Click();
        cut.Find("[data-testid=creative-start]").Click();

        Assert.Equal((PropertyId, VerticalCreativeTestMode.HookAb, (VerticalCreativeFormat?)VerticalCreativeFormat.PriceFirst), creative.Started.Single());
        Assert.Equal(CampaignCaptionStyle.Comic, creative.StartedCaptionStyles.Single());
        Assert.Contains("The creative test is queued", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ResultsAreGroupedBySuiteAndReviewedByDeliverableAndVersion()
    {
        creative.Suites = [Suite()];
        var cut = context.Render<AdminCreativeTestingTab>();

        Assert.Contains("1 Main St, Raleigh, NC", cut.Markup, StringComparison.Ordinal);
        var passed = cut.Find("[data-testid=creative-case-PriceFirst-a]");
        Assert.Contains("Technical check passed", passed.TextContent, StringComparison.Ordinal);
        Assert.Contains("Awaiting review", passed.TextContent, StringComparison.Ordinal);
        Assert.Contains($"/campaigns/videos/{DeliverableA}?v=2", passed.InnerHtml, StringComparison.Ordinal);
        var failed = cut.Find("[data-testid=creative-case-PriceFirst-b]");
        Assert.Contains("Technical findings:", failed.TextContent, StringComparison.Ordinal);
        Assert.Empty(failed.QuerySelectorAll("video"));
        Assert.Single(cut.FindComponents<Stub<CampaignVideoChatPanel>>());
        Assert.Equal("1", cut.Find("[data-testid=creative-summary] div:nth-child(2) dd").TextContent);

        cut.Find("[data-testid=creative-approve-PriceFirst-a]").Click();
        Assert.Equal((DeliverableA, 2, CampaignVideoReviewDecision.Approved), reviews.Decisions.Single());
        Assert.Contains("version 2 approved", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ASwitchedOffWorkspaceSeesWhyInsteadOfControlsFailingSilently()
    {
        creative.Disabled = true;
        var cut = context.Render<AdminCreativeTestingTab>();
        Assert.Contains("switched off", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void PublishedTestsShowTheirComparisonAndTheRuleEvaluationNeedsTheMinimumSample()
    {
        var thin = Report(meets: false);
        var enough = Report(meets: true);
        evaluation.Reports = [enough, thin];
        evaluation.Performance =
        [
            new VerticalPerformanceView("Creative test started 2026-11-01 10:00 UTC", PropertyId, null, Guid.NewGuid(),
                VerticalPerformanceCalculator.CalculationVersion, VerticalPerformanceCalculator.Compare(
                [
                    (Guid.NewGuid(), DeliverableA, VerticalCreativeFormat.PriceFirst, "a", VerticalCampaignPurpose.Consideration,
                        [Post(new CampaignMetricValues(4_000, 360, 0, 20, 15, null, null, 40, 1, 0))]),
                    (Guid.NewGuid(), Guid.NewGuid(), VerticalCreativeFormat.PriceFirst, "b", VerticalCampaignPurpose.Consideration,
                        [Post(new CampaignMetricValues(2_000, 100, 0, 6, 8, null, 0.25m, 60, 5, 2))]),
                ])),
        ];
        var cut = context.Render<AdminCreativeTestingTab>();

        Assert.Equal("2", cut.Find("[data-testid=creative-summary] div:nth-child(4) dd").TextContent);
        Assert.Contains("Most leads", cut.Find("[data-testid=performance-row-PriceFirst-b]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Most reach", cut.Find("[data-testid=performance-row-PriceFirst-a]").TextContent, StringComparison.Ordinal);
        Assert.Equal(VerticalFormatRules.Version, cut.Find("[data-testid=active-rule-version]").TextContent);
        Assert.NotNull(cut.Find($"[data-testid=rule-report-{thin.Id}] [data-testid=below-minimum]"));
        Assert.Empty(cut.FindAll($"[data-testid=promote-{thin.Id}]"));

        cut.Find("[data-testid=create-rule-draft]").Click();
        Assert.Equal(1, evaluation.Drafts);
        Assert.Contains("below the minimum sample", cut.Markup, StringComparison.Ordinal);

        cut.Find($"[data-testid=promote-{enough.Id}]").Click();
        Assert.Equal((enough.Id, VerticalFormatRules.Version), evaluation.Promoted.Single());
    }

    private static VerticalPerformancePost Post(CampaignMetricValues values) =>
        new(Guid.NewGuid(), DistributionPlatform.TikTok, "https://www.tiktok.com/@a/video/1", DateTimeOffset.UnixEpoch, 1, DateTimeOffset.UnixEpoch, values);

    private static VerticalRuleEvaluationView Report(bool meets) => new(Guid.NewGuid(), DateTimeOffset.UnixEpoch, VerticalFormatRules.Version,
        VerticalPerformanceCalculator.CalculationVersion, meets ? 40 : 2, meets ? 90_000 : 6_000, meets, VerticalRuleReportStatus.Draft, null, null,
        [new VerticalRuleSegment("Format", "All listings", VerticalCreativeFormat.PriceFirst, meets ? 40 : 2, meets ? 90_000 : 6_000, 0.05m, 0.01m,
            0.02m, 6, 75m, meets)]);

    private sealed class FakeEvaluation : IVerticalRuleEvaluationService
    {
        public IReadOnlyList<VerticalPerformanceView> Performance { get; set; } = [];

        public IReadOnlyList<VerticalRuleEvaluationView> Reports { get; set; } = [];

        public int Drafts { get; private set; }

        public List<(Guid, string)> Promoted { get; } = [];

        public Task<IReadOnlyList<VerticalPerformanceView>> GetSuitePerformanceAsync(string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Performance);

        public Task<VerticalRuleEvaluationView> CreateDraftAsync(string userId, CancellationToken cancellationToken = default)
        {
            Drafts++;
            return Task.FromResult(Report(meets: false));
        }

        public Task<IReadOnlyList<VerticalRuleEvaluationView>> ListAsync(string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Reports);

        public Task PromoteAsync(string userId, Guid reportId, string ruleVersion, CancellationToken cancellationToken = default)
        {
            Promoted.Add((reportId, ruleVersion));
            return Task.CompletedTask;
        }

        public Task<string> GetActiveRuleVersionAsync(string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(VerticalFormatRules.Version);
    }

    private static VerticalCreativeTestSuiteView Suite() => new(Guid.NewGuid(), PropertyId, "1 Main St, Raleigh, NC",
        VerticalCreativeTestMode.HookAb, VerticalCreativeFormat.PriceFirst, "vertical-rules-v1", VerticalCreativeTestSuiteStatus.Completed,
        null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
        [
            new VerticalCreativeTestCaseView(Guid.NewGuid(), 0, VerticalCreativeFormat.PriceFirst, "a", VerticalCampaignPurpose.Consideration, 75,
                ["The listing price is entered."], "price-first-v1", 30, VerticalCreativeTestCaseStatus.Passed, null, Guid.NewGuid(), Guid.NewGuid(),
                DeliverableA, "$450,000 in Raleigh, NC", 2, null, [], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            new VerticalCreativeTestCaseView(Guid.NewGuid(), 1, VerticalCreativeFormat.PriceFirst, "b", VerticalCampaignPurpose.Consideration, 75,
                [], "price-first-v1", 30, VerticalCreativeTestCaseStatus.Failed, "Failed: The file did not decode cleanly.", Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), "What $450,000 gets you in Raleigh, NC", 1, null, [], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
        ]);

    private sealed class FakeCreativeTests : IVerticalCreativeTestService
    {
        public IReadOnlyList<VerticalCreativeTestSuiteView> Suites { get; set; } = [];

        public bool Disabled { get; set; }

        public List<(Guid, VerticalCreativeTestMode, VerticalCreativeFormat?)> Started { get; } = [];

        public Task<bool> IsEnabledAsync(string userId, CancellationToken cancellationToken = default) => Task.FromResult(!Disabled);

        public Task<VerticalCreativeTestPreview?> PreviewAsync(string userId, Guid propertyId, CancellationToken cancellationToken = default) =>
            Task.FromResult<VerticalCreativeTestPreview?>(VerticalCreativeTestPlanner.Preview(propertyId, "1 Main St, Raleigh, NC",
                VerticalCreativePlan.Create(RecipeFixture.Rich())));

        public Task<VerticalCreativeTestSuiteView> StartAsync(string userId, Guid propertyId, VerticalCreativeTestMode mode,
            VerticalCreativeFormat? format, CampaignCaptionStyle captionStyle = CampaignCaptionStyles.Default,
            CancellationToken cancellationToken = default)
        {
            Started.Add((propertyId, mode, format));
            StartedCaptionStyles.Add(captionStyle);
            return Task.FromResult(Suite());
        }

        public List<CampaignCaptionStyle> StartedCaptionStyles { get; } = [];

        public Task CancelAsync(string userId, Guid suiteId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<VerticalCreativeTestSuiteView>> ListAsync(string userId, CancellationToken cancellationToken = default) =>
            Disabled ? throw new CreativeTestingDisabledException() : Task.FromResult(Suites);
    }

    private sealed class FakeReviews : ICampaignReviewService
    {
        public List<(Guid, int, CampaignVideoReviewDecision)> Decisions { get; } = [];

        public Task<IReadOnlyList<CampaignReviewItem>> ListAsync(string userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CampaignReviewItem>> ListSessionAsync(string userId, Guid suiteId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DecideAsync(string userId, Guid jobId, int expectedVersion, CampaignVideoReviewDecision decision, string? feedback,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DecideDeliverableAsync(string userId, Guid deliverableId, int expectedVersion, CampaignVideoReviewDecision decision,
            string? feedback, CancellationToken cancellationToken = default)
        {
            Decisions.Add((deliverableId, expectedVersion, decision));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeProperties : ITikTokTestSuiteService
    {
        public Task<IReadOnlyList<TikTokTestPropertyChoice>> PropertyChoicesAsync(string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TikTokTestPropertyChoice>>([new TikTokTestPropertyChoice(PropertyId, "1 Main St, Raleigh, NC")]);

        public Task<IReadOnlyList<TikTokTestSuiteResult>> ListAsync(string userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TikTokTestSuiteResult> StartAsync(string userId, Guid propertyId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SetArchivedAsync(string userId, Guid suiteId, bool archived, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CancelAsync(string userId, Guid suiteId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
