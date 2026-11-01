using ListingStudio.Application.Campaigns;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Properties;
using Xunit;

namespace ListingStudio.UnitTests;

/// <summary>Prompt 10: metric validation, the calculation, CSV parsing and the rule evaluation's minimum sample.</summary>
public sealed class VerticalPerformanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(DistributionPlatform.TikTok, "https://www.tiktok.com/@a/video/1?lang=en#x", "https://www.tiktok.com/@a/video/1")]
    [InlineData(DistributionPlatform.InstagramReels, "https://instagram.com/reel/abc/", "https://instagram.com/reel/abc")]
    [InlineData(DistributionPlatform.YouTubeShorts, "https://youtu.be/xyz", "https://youtu.be/xyz")]
    [InlineData(DistributionPlatform.YouTubeShorts, "https://m.youtube.com/shorts/xyz", "https://m.youtube.com/shorts/xyz")]
    public void PostLinksKeepOnlyThePlatformPath(DistributionPlatform platform, string raw, string expected) =>
        Assert.Equal(expected, CampaignDistributionRecord.NormalizePostUrl(platform, raw));

    [Theory]
    [InlineData(DistributionPlatform.TikTok, "http://www.tiktok.com/@a/video/1")]
    [InlineData(DistributionPlatform.TikTok, "https://tiktok.com.evil.example/@a/video/1")]
    [InlineData(DistributionPlatform.TikTok, "https://www.instagram.com/reel/1")]
    [InlineData(DistributionPlatform.InstagramReels, "not a link")]
    [InlineData(DistributionPlatform.YouTubeShorts, "")]
    public void PostLinksMustBeHttpsOnThePlatform(DistributionPlatform platform, string raw) =>
        Assert.Throws<ArgumentException>(() => CampaignDistributionRecord.NormalizePostUrl(platform, raw));

    [Fact]
    public void MetricValuesAreValidatedAndSnapshotsKeepTheSecond()
    {
        Assert.Throws<ArgumentException>(() => new CampaignMetricValues(-1, 0, 0, 0, 0, null, null, 0, 0, 0).Validate());
        Assert.Throws<ArgumentException>(() => new CampaignMetricValues(10, 11, 0, 0, 0, null, null, 0, 0, 0).Validate());
        Assert.Throws<ArgumentException>(() => new CampaignMetricValues(10, 1, 0, 0, 0, null, null, 11, 0, 0).Validate());
        Assert.Throws<ArgumentException>(() => new CampaignMetricValues(10, 1, 0, 0, 0, null, 1.2m, 1, 0, 0).Validate());
        Assert.Throws<ArgumentException>(() => new CampaignMetricValues(10, 1, 0, 0, 0, -1m, null, 1, 0, 0).Validate());
        new CampaignMetricValues(10, 1, 0, 0, 0, 7.5m, 0.4m, 1, 3, 1).Validate();

        var record = Record(Now.AddDays(-1));
        Assert.Throws<ArgumentException>(() => CampaignMetricSnapshot.Create(record, Values(10, 1), Now.AddDays(-2), CampaignMetricSource.Manual, "u", Now));
        Assert.Throws<ArgumentException>(() => CampaignMetricSnapshot.Create(record, Values(10, 1), Now.AddHours(1), CampaignMetricSource.Manual, "u", Now));
        var snapshot = CampaignMetricSnapshot.Create(record, Values(10, 1), Now.AddMilliseconds(-750), CampaignMetricSource.CsvImport, "u", Now);
        Assert.Equal(Now.AddSeconds(-1), snapshot.CapturedAtUtc);
        Assert.Equal(record.OrganizationId, snapshot.OrganizationId);
        Assert.Throws<ArgumentException>(() => Record(Now.AddHours(1)));
    }

    [Fact]
    public void ComparisonSeparatesReachFromBusinessResults()
    {
        var reach = Guid.NewGuid();
        var leads = Guid.NewGuid();
        var unposted = Guid.NewGuid();
        var videos = VerticalPerformanceCalculator.Compare(
        [
            (reach, Guid.NewGuid(), VerticalCreativeFormat.WowFeature, "a", VerticalCampaignPurpose.Reach,
                [Post(new CampaignMetricValues(8_000, 800, 40, 60, 30, 8m, 0.20m, 80, 1, 0)),
                 Post(new CampaignMetricValues(2_000, 100, 10, 10, 10, null, 0.40m, 20, 0, 0))]),
            (leads, Guid.NewGuid(), VerticalCreativeFormat.PriceFirst, "a", VerticalCampaignPurpose.Consideration,
                [Post(new CampaignMetricValues(2_000, 60, 5, 4, 20, 12m, 0.50m, 100, 6, 2))]),
            (unposted, Guid.NewGuid(), VerticalCreativeFormat.StoryReveal, "a", VerticalCampaignPurpose.Reach, []),
        ]);

        var a = videos.Single(video => video.VariantId == reach);
        Assert.Equal(10_000, a.Totals.Views);
        Assert.Equal(0.09m, a.Rates.LikeRate);
        // Completion is view-weighted, and only posts that report it count.
        Assert.Equal(0.24m, a.Rates.CompletionRate);
        Assert.Equal(0.01m, a.Rates.ClickThroughRate);
        Assert.True(a.IsAwarenessLeader);
        Assert.False(a.IsBusinessLeader);
        Assert.Equal(4m, a.Uplift.Views);

        var b = videos.Single(video => video.VariantId == leads);
        Assert.True(b.IsBusinessLeader);
        Assert.False(b.IsAwarenessLeader);
        Assert.Equal(0.03m, b.Rates.LikeRate);
        Assert.Equal(0.06m, b.Rates.LeadConversion);
        Assert.Equal(5m, b.Uplift.Leads);
        Assert.Equal(-0.8m, b.Uplift.Views);

        var c = videos.Single(video => video.VariantId == unposted);
        Assert.False(c.HasMetrics);
        Assert.Null(c.Rates.LikeRate);
        Assert.Null(c.Uplift.Views);
        Assert.False(c.IsAwarenessLeader || c.IsBusinessLeader);
    }

    [Fact]
    public void CsvRowsAreParsedOrRejectedLineByLine()
    {
        const string header = "post_url,platform,captured_at,views,likes,comments,shares,saves,average_watch_seconds,completion_rate,clicks,leads,booked_showings";
        var (rows, errors) = MetricCsvParser.Parse(new StringReader(header + "\n"
            + "\"https://www.tiktok.com/@a/video/1?x=1\",TikTok,2026-11-01 10:00,\"1,200\",100,3,4,5,9.5,42%,20,2,1\n"
            + "https://instagram.com/reel/abc,Instagram Reels,2026-11-01T10:00:00Z,50,5,0,0,0,,0.5,0,0,0\n"));
        Assert.Empty(errors);
        Assert.Equal(2, rows.Count);
        Assert.Equal("https://www.tiktok.com/@a/video/1", rows[0].PostUrl);
        Assert.Equal(1_200, rows[0].Values.Views);
        Assert.Equal(0.42m, rows[0].Values.CompletionRate);
        Assert.Equal(DistributionPlatform.InstagramReels, rows[1].Platform);
        Assert.Null(rows[1].Values.AverageWatchSeconds);

        var (_, bad) = MetricCsvParser.Parse(new StringReader(header + "\n"
            + "https://www.tiktok.com/@a/video/1,TikTok,yesterday,10,1,0,0,0,,,0,0,0\n"
            + "https://www.tiktok.com/@a/video/1,Snapchat,2026-11-01,10,1,0,0,0,,,0,0,0\n"
            + "https://www.tiktok.com/@a/video/1,TikTok,2026-11-01,10,20,0,0,0,,,0,0,0\n"
            + "https://www.tiktok.com/@a/video/2,TikTok,2026-11-01,10,1,0,0,0,,,0,0,0\n"
            + "https://www.tiktok.com/@a/video/2?y=2,TikTok,2026-11-01,11,1,0,0,0,,,0,0,0\n"));
        Assert.Contains(bad, line => line.StartsWith("Line 2: captured_at", StringComparison.Ordinal));
        Assert.Contains(bad, line => line.StartsWith("Line 3: platform", StringComparison.Ordinal));
        Assert.Contains(bad, line => line.StartsWith("Line 4: Likes", StringComparison.Ordinal));
        Assert.Contains(bad, line => line.StartsWith("Lines 5 and 6", StringComparison.Ordinal));

        var (_, missing) = MetricCsvParser.Parse(new StringReader("post_url,views\n"));
        Assert.StartsWith("Line 1: missing columns platform, captured_at", Assert.Single(missing), StringComparison.Ordinal);
    }

    [Fact]
    public void RuleEvaluationNeedsTheDocumentedMinimumSampleAndNeverStoresAgentIdentity()
    {
        var thin = Enumerable.Range(0, 12).Select(index => Input(VerticalCreativeFormat.PriceFirst, 1_000, $"Agent {index % 2 + 1}")).ToArray();
        var (segments, meets) = VerticalPerformanceCalculator.Evaluate(thin);
        Assert.False(meets);
        Assert.Contains(segments, segment => segment.Dimension == "Format" && segment.Videos == 12 && segment.MeetsMinimumSample);
        Assert.Contains(segments, segment => segment.Dimension == "Price band" && segment.Value == "$300k–$600k");
        Assert.Contains(segments, segment => segment.Dimension == "Media availability" && segment.Value == "12+ photos without video");
        Assert.Contains(segments, segment => segment.Dimension == "Agent account" && segment.Value == "Agent 2");

        var lopsided = thin.Concat(Enumerable.Range(0, 20).Select(_ => Input(VerticalCreativeFormat.WowFeature, 1_000, "Agent 1")))
            .Append(Input(VerticalCreativeFormat.StoryReveal, 90_000, "Agent 1")).ToArray();
        Assert.False(VerticalPerformanceCalculator.Evaluate(lopsided).MeetsMinimumSample);

        var enough = thin.Concat(Enumerable.Range(0, 20).Select(_ => Input(VerticalCreativeFormat.WowFeature, 1_000, "Agent 1"))).ToArray();
        var result = VerticalPerformanceCalculator.Evaluate(enough);
        Assert.True(result.MeetsMinimumSample);
        var json = VerticalPerformanceCalculator.ToJson(result.Segments);
        Assert.Equal(result.Segments, VerticalPerformanceCalculator.FromJson(json));
        Assert.Empty(VerticalPerformanceCalculator.FromJson("not json"));
    }

    [Fact]
    public void ReportsArePromotedOnceAndOnlyWithTheMinimumSample()
    {
        var thin = VerticalRuleEvaluationReport.CreateDraft(Guid.NewGuid(), "admin", VerticalFormatRules.Version,
            VerticalPerformanceCalculator.CalculationVersion, 3, 900, false, "[]", Now);
        Assert.Throws<InvalidOperationException>(() => thin.Promote(VerticalFormatRules.Version, "admin", Now));
        Assert.Equal(VerticalRuleReportStatus.Draft, thin.Status);

        var enough = VerticalRuleEvaluationReport.CreateDraft(Guid.NewGuid(), "admin", VerticalFormatRules.Version,
            VerticalPerformanceCalculator.CalculationVersion, 40, 90_000, true, "[]", Now);
        enough.Promote(VerticalFormatRules.Version, "admin", Now);
        Assert.Equal((VerticalRuleReportStatus.Promoted, VerticalFormatRules.Version), (enough.Status, enough.PromotedRuleVersion));
        Assert.Throws<InvalidOperationException>(() => enough.Promote(VerticalFormatRules.Version, "admin", Now));
        Assert.Contains(VerticalFormatRules.Version, VerticalPerformanceCalculator.ImplementedRuleVersions);
        Assert.DoesNotContain("vertical-rules-v2", VerticalPerformanceCalculator.ImplementedRuleVersions);
    }

    private static CampaignDistributionRecord Record(DateTimeOffset published) => CampaignDistributionRecord.Create(Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DistributionPlatform.TikTok, "https://www.tiktok.com/@a/video/1", published, "u", Now);

    private static CampaignMetricValues Values(long views, long likes) => new(views, likes, 0, 0, 0, null, null, 0, 0, 0);

    private static VerticalPerformancePost Post(CampaignMetricValues values) =>
        new(Guid.NewGuid(), DistributionPlatform.TikTok, "https://www.tiktok.com/@a/video/1", Now, 1, Now, values);

    private static VerticalRuleEvaluationInput Input(VerticalCreativeFormat format, long views, string agent) =>
        new(format, PropertyType.SingleFamily, 450_000m, 14, false, agent, 70,
            new VerticalMetricTotals(views, views / 10, 0, 0, 0, null, null, views / 50, 1, 0));
}
