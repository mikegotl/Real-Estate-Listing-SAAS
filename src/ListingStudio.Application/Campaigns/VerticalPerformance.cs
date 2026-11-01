using System.Globalization;
using System.Text.Json;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Properties;

namespace ListingStudio.Application.Campaigns;

/// <summary>
/// Performance entry and comparison for vertical library videos (manual entry and CSV only; no platform access).
/// Every call needs VerticalVideoLibraryV1 on for the caller's workspace and throws
/// <see cref="PerformanceTrackingDisabledException"/> otherwise. Turning the switch off never deletes posts,
/// snapshots or reports.
/// </summary>
public interface IVerticalPerformanceService
{
    Task<bool> IsEnabledAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>The library's videos, their posts and latest numbers, compared within the campaign. Null for a legacy campaign.</summary>
    Task<VerticalPerformanceView?> GetCampaignAsync(string userId, Guid jobId, CancellationToken cancellationToken = default);

    Task<Guid> AddDistributionAsync(string userId, Guid deliverableId, DistributionPlatform platform, string postUrl,
        DateTimeOffset publishedAtUtc, CancellationToken cancellationToken = default);

    Task AddSnapshotAsync(string userId, Guid distributionId, CampaignMetricValues values, DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken = default);

    /// <summary>Validates every row first; imports all of them or none.</summary>
    Task<MetricImportResult> ImportCsvAsync(string userId, Stream csv, CancellationToken cancellationToken = default);
}

/// <summary>Admin-only measurement views and the draft rule evaluation (platform administrators, current workspace).</summary>
public interface IVerticalRuleEvaluationService
{
    Task<IReadOnlyList<VerticalPerformanceView>> GetSuitePerformanceAsync(string userId, CancellationToken cancellationToken = default);

    Task<VerticalRuleEvaluationView> CreateDraftAsync(string userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VerticalRuleEvaluationView>> ListAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Makes a rule version active for the workspace. Needs a report that meets the minimum sample and an implemented rule version.</summary>
    Task PromoteAsync(string userId, Guid reportId, string ruleVersion, CancellationToken cancellationToken = default);

    Task<string> GetActiveRuleVersionAsync(string userId, CancellationToken cancellationToken = default);
}

public sealed class PerformanceTrackingDisabledException()
    : InvalidOperationException("Performance tracking is switched off for this workspace.");

public sealed record MetricImportResult(int Imported, IReadOnlyList<string> Errors)
{
    public bool Succeeded => Errors.Count == 0;
}

/// <summary>One video's raw totals (latest snapshot per post, summed across its posts).</summary>
public sealed record VerticalMetricTotals(
    long Views,
    long Likes,
    long Comments,
    long Shares,
    long Saves,
    decimal? AverageWatchSeconds,
    decimal? CompletionRate,
    long Clicks,
    int Leads,
    int BookedShowings)
{
    public static VerticalMetricTotals Sum(IReadOnlyList<CampaignMetricValues> latest)
    {
        static decimal? Weighted(IReadOnlyList<CampaignMetricValues> values, Func<CampaignMetricValues, decimal?> pick)
        {
            var known = values.Where(value => pick(value) is not null && value.Views > 0).ToArray();
            var views = known.Sum(value => value.Views);
            return views == 0 ? null : Math.Round(known.Sum(value => pick(value)!.Value * value.Views) / views, 4);
        }

        return new VerticalMetricTotals(
            latest.Sum(value => value.Views), latest.Sum(value => value.Likes), latest.Sum(value => value.Comments),
            latest.Sum(value => value.Shares), latest.Sum(value => value.Saves),
            Weighted(latest, value => value.AverageWatchSeconds), Weighted(latest, value => value.CompletionRate),
            latest.Sum(value => value.Clicks), latest.Sum(value => value.Leads), latest.Sum(value => value.BookedShowings));
    }
}

/// <summary>Rates worked out from the totals under one calculation version. Null where the denominator is zero.</summary>
public sealed record VerticalMetricRates(
    decimal? LikeRate,
    decimal? ShareRate,
    decimal? SaveRate,
    decimal? CompletionRate,
    decimal? ClickThroughRate,
    decimal? LeadConversion)
{
    public static VerticalMetricRates From(VerticalMetricTotals totals)
    {
        static decimal? Rate(long part, long whole) => whole <= 0 ? null : Math.Round((decimal)part / whole, 4);
        return new VerticalMetricRates(
            Rate(totals.Likes, totals.Views), Rate(totals.Shares, totals.Views), Rate(totals.Saves, totals.Views),
            totals.CompletionRate, Rate(totals.Clicks, totals.Views), Rate(totals.Leads, totals.Clicks));
    }
}

/// <summary>A rate against the average of the other videos it is compared with: 0.25 means 25% higher. Null without a basis.</summary>
public sealed record VerticalMetricUplift(
    decimal? Views,
    decimal? LikeRate,
    decimal? ShareRate,
    decimal? SaveRate,
    decimal? CompletionRate,
    decimal? ClickThroughRate,
    decimal? Leads);

public sealed record VerticalPerformancePost(
    Guid Id,
    DistributionPlatform Platform,
    string PostUrl,
    DateTimeOffset PublishedAtUtc,
    int SnapshotCount,
    DateTimeOffset? LatestCapturedAtUtc,
    CampaignMetricValues? Latest);

public sealed record VerticalPerformanceVideo(
    Guid VariantId,
    Guid DeliverableId,
    VerticalCreativeFormat Format,
    string HookVariant,
    VerticalCampaignPurpose Purpose,
    IReadOnlyList<VerticalPerformancePost> Posts,
    VerticalMetricTotals Totals,
    VerticalMetricRates Rates,
    VerticalMetricUplift Uplift,
    bool IsAwarenessLeader,
    bool IsBusinessLeader)
{
    public bool HasMetrics => Posts.Any(post => post.Latest is not null);
}

/// <summary>One comparison group: a campaign's library, or a creative test (same property, same suite).</summary>
public sealed record VerticalPerformanceView(
    string GroupLabel,
    Guid PropertyId,
    Guid? JobId,
    Guid? SuiteId,
    string CalculationVersion,
    IReadOnlyList<VerticalPerformanceVideo> Videos)
{
    public bool HasMetrics => Videos.Any(video => video.HasMetrics);
}

public sealed record VerticalRuleSegment(
    string Dimension,
    string Value,
    VerticalCreativeFormat Format,
    int Videos,
    long Views,
    decimal? LikeRate,
    decimal? ShareRate,
    decimal? ClickThroughRate,
    int Leads,
    decimal? AverageRuleScore,
    bool MeetsMinimumSample);

public sealed record VerticalRuleEvaluationView(
    Guid Id,
    DateTimeOffset CreatedAtUtc,
    string BaseRuleVersion,
    string CalculationVersion,
    int VideoCount,
    long TotalViews,
    bool MeetsMinimumSample,
    VerticalRuleReportStatus Status,
    string? PromotedRuleVersion,
    DateTimeOffset? PromotedAtUtc,
    IReadOnlyList<VerticalRuleSegment> Segments);

/// <summary>The input of one measured video to the rule evaluation: grouping keys only, no listing text.</summary>
public sealed record VerticalRuleEvaluationInput(
    VerticalCreativeFormat Format,
    PropertyType PropertyType,
    decimal ListingPrice,
    int PhotoCount,
    bool HasPropertyVideo,
    string AgentKey,
    int RuleScore,
    VerticalMetricTotals Totals);

public static class VerticalPerformanceCalculator
{
    public const string CalculationVersion = "performance-calc-v1";

    /// <summary>
    /// The documented minimum sample before a rule version can be promoted: at least <see cref="MinimumVideosPerSegment"/>
    /// measured videos and <see cref="MinimumViewsPerSegment"/> views in every format segment, and at least
    /// <see cref="MinimumReportVideos"/> measured videos in the report.
    /// </summary>
    public const int MinimumVideosPerSegment = 10;

    public const long MinimumViewsPerSegment = 5_000;

    public const int MinimumReportVideos = 30;

    /// <summary>Rule versions the recommendation engine implements; only these can be promoted.</summary>
    public static IReadOnlySet<string> ImplementedRuleVersions { get; } = new HashSet<string>(StringComparer.Ordinal) { VerticalFormatRules.Version };

    private static readonly JsonSerializerOptions SegmentOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>
    /// Compares the videos of one group. The awareness leader has the most views (then the best like rate); the
    /// business leader has the most leads (then booked showings, then clicks). They are often different videos, and
    /// both are flagged, so a video with fewer likes but more qualified leads stays visible as the business winner.
    /// </summary>
    public static IReadOnlyList<VerticalPerformanceVideo> Compare(
        IReadOnlyList<(Guid VariantId, Guid DeliverableId, VerticalCreativeFormat Format, string HookVariant, VerticalCampaignPurpose Purpose,
            IReadOnlyList<VerticalPerformancePost> Posts)> videos)
    {
        var measured = videos.Select(video =>
        {
            var totals = VerticalMetricTotals.Sum(video.Posts.Select(post => post.Latest).OfType<CampaignMetricValues>().ToArray());
            return (video, totals, rates: VerticalMetricRates.From(totals), hasMetrics: video.Posts.Any(post => post.Latest is not null));
        }).ToArray();
        var withMetrics = measured.Where(item => item.hasMetrics).ToArray();
        var awareness = withMetrics.Where(item => item.totals.Views > 0)
            .OrderByDescending(item => item.totals.Views).ThenByDescending(item => item.rates.LikeRate ?? 0).FirstOrDefault();
        var business = withMetrics.Where(item => item.totals.Leads > 0 || item.totals.BookedShowings > 0 || item.totals.Clicks > 0)
            .OrderByDescending(item => item.totals.Leads).ThenByDescending(item => item.totals.BookedShowings)
            .ThenByDescending(item => item.totals.Clicks).FirstOrDefault();
        return measured.Select(item =>
        {
            var others = withMetrics.Where(other => other.video.VariantId != item.video.VariantId).ToArray();
            decimal? Uplift(Func<(VerticalMetricTotals totals, VerticalMetricRates rates), decimal?> pick)
            {
                var mine = pick((item.totals, item.rates));
                var values = others.Select(other => pick((other.totals, other.rates))).OfType<decimal>().ToArray();
                if (!item.hasMetrics || mine is null || values.Length == 0)
                {
                    return null;
                }

                var basis = values.Average();
                return basis == 0 ? null : Math.Round((mine.Value - basis) / basis, 4);
            }

            var uplift = new VerticalMetricUplift(
                Uplift(value => value.totals.Views), Uplift(value => value.rates.LikeRate), Uplift(value => value.rates.ShareRate),
                Uplift(value => value.rates.SaveRate), Uplift(value => value.rates.CompletionRate),
                Uplift(value => value.rates.ClickThroughRate), Uplift(value => value.totals.Leads));
            return new VerticalPerformanceVideo(item.video.VariantId, item.video.DeliverableId, item.video.Format, item.video.HookVariant,
                item.video.Purpose, item.video.Posts, item.totals, item.rates, uplift,
                awareness.video.VariantId == item.video.VariantId && awareness.hasMetrics,
                business.video.VariantId == item.video.VariantId && business.hasMetrics);
        }).ToArray();
    }

    public static string PriceBand(decimal price) => price switch
    {
        < 300_000m => "Under $300k",
        < 600_000m => "$300k–$600k",
        < 1_000_000m => "$600k–$1M",
        _ => "$1M and up",
    };

    public static string MediaAvailability(int photos, bool hasVideo) =>
        (photos >= 12 ? "12+ photos" : "Under 12 photos") + (hasVideo ? " with video" : " without video");

    /// <summary>Segments by format and each dimension; a segment meets the sample only with enough videos and views.</summary>
    public static (IReadOnlyList<VerticalRuleSegment> Segments, bool MeetsMinimumSample) Evaluate(IReadOnlyList<VerticalRuleEvaluationInput> inputs)
    {
        var segments = new List<VerticalRuleSegment>();
        void Add(string dimension, Func<VerticalRuleEvaluationInput, string> key)
        {
            foreach (var group in inputs.GroupBy(input => (key(input), input.Format)).OrderBy(group => group.Key.Format).ThenBy(group => group.Key.Item1, StringComparer.Ordinal))
            {
                var totals = VerticalMetricTotals.Sum(group.Select(input => new CampaignMetricValues(input.Totals.Views, input.Totals.Likes,
                    input.Totals.Comments, input.Totals.Shares, input.Totals.Saves, input.Totals.AverageWatchSeconds, input.Totals.CompletionRate,
                    input.Totals.Clicks, input.Totals.Leads, input.Totals.BookedShowings)).ToArray());
                var rates = VerticalMetricRates.From(totals);
                var count = group.Count();
                segments.Add(new VerticalRuleSegment(dimension, group.Key.Item1, group.Key.Format, count, totals.Views, rates.LikeRate,
                    rates.ShareRate, rates.ClickThroughRate, totals.Leads, Math.Round((decimal)group.Average(input => input.RuleScore), 1),
                    count >= MinimumVideosPerSegment && totals.Views >= MinimumViewsPerSegment));
            }
        }

        Add("Format", _ => "All listings");
        Add("Property type", input => input.PropertyType.ToString());
        Add("Price band", input => PriceBand(input.ListingPrice));
        Add("Media availability", input => MediaAvailability(input.PhotoCount, input.HasPropertyVideo));
        Add("Agent account", input => input.AgentKey);
        var formatSegments = segments.Where(segment => segment.Dimension == "Format").ToArray();
        var meets = inputs.Count >= MinimumReportVideos && formatSegments.Length > 0 && formatSegments.All(segment => segment.MeetsMinimumSample);
        return (segments, meets);
    }

    public static string ToJson(IReadOnlyList<VerticalRuleSegment> segments) => JsonSerializer.Serialize(segments, SegmentOptions);

    public static IReadOnlyList<VerticalRuleSegment> FromJson(string? json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<VerticalRuleSegment[]>(json, SegmentOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>One parsed CSV row, matched to a post by its URL.</summary>
public sealed record MetricCsvRow(int Line, DistributionPlatform Platform, string PostUrl, DateTimeOffset CapturedAtUtc, CampaignMetricValues Values);

/// <summary>
/// Reads the metrics CSV. Header (any order, case-insensitive):
/// platform, post_url, captured_at, views, likes, comments, shares, saves, average_watch_seconds, completion_rate,
/// clicks, leads, booked_showings. average_watch_seconds and completion_rate may be blank; completion_rate is 0–1 or
/// a percentage like 42%. Errors name the line; nothing is imported if any row is wrong.
/// </summary>
public static class MetricCsvParser
{
    public const int MaximumRows = 2_000;

    private static readonly string[] Required =
        ["platform", "post_url", "captured_at", "views", "likes", "comments", "shares", "saves", "clicks", "leads", "booked_showings"];

    public static (IReadOnlyList<MetricCsvRow> Rows, IReadOnlyList<string> Errors) Parse(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var errors = new List<string>();
        var rows = new List<MetricCsvRow>();
        var header = reader.ReadLine();
        if (header is null)
        {
            return ([], ["The file is empty."]);
        }

        var columns = Split(header.TrimStart('﻿')).Select(name => name.Trim().ToLowerInvariant()).ToArray();
        var missing = Required.Where(name => !columns.Contains(name)).ToArray();
        if (missing.Length > 0)
        {
            return ([], [$"Line 1: missing column{(missing.Length == 1 ? string.Empty : "s")} {string.Join(", ", missing)}."]);
        }

        int Index(string name) => Array.IndexOf(columns, name);
        var line = 1;
        string? text;
        while ((text = reader.ReadLine()) is not null)
        {
            line++;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (rows.Count + errors.Count >= MaximumRows)
            {
                errors.Add($"The file has more than {MaximumRows} rows. Split it and import each part.");
                break;
            }

            var cells = Split(text);
            string Cell(string name) => Index(name) is var at && at >= 0 && at < cells.Count ? cells[at].Trim() : string.Empty;
            try
            {
                var platform = Cell("platform").Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant() switch
                {
                    "tiktok" => DistributionPlatform.TikTok,
                    "instagram" or "instagramreels" or "reels" => DistributionPlatform.InstagramReels,
                    "youtube" or "youtubeshorts" or "shorts" => DistributionPlatform.YouTubeShorts,
                    _ => throw new FormatException("platform must be TikTok, Instagram Reels or YouTube Shorts"),
                };
                var url = CampaignDistributionRecord.NormalizePostUrl(platform, Cell("post_url"));
                if (!DateTimeOffset.TryParse(Cell("captured_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var captured))
                {
                    throw new FormatException("captured_at must be a date and time such as 2026-11-01 14:30");
                }

                var values = new CampaignMetricValues(Long("views"), Long("likes"), Long("comments"), Long("shares"), Long("saves"),
                    OptionalDecimal("average_watch_seconds"), Completion(Cell("completion_rate")), Long("clicks"), (int)Long("leads"),
                    (int)Long("booked_showings"));
                values.Validate();
                rows.Add(new MetricCsvRow(line, platform, url, captured.ToUniversalTime(), values));
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException or OverflowException)
            {
                errors.Add($"Line {line}: {exception.Message.Split(" (Parameter", 2)[0].TrimEnd('.')}.");
            }

            long Long(string name) => long.TryParse(Cell(name).Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var value) ? value : throw new FormatException($"{name} must be a whole number");
            decimal? OptionalDecimal(string name) => Cell(name) is { Length: > 0 } raw
                ? decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : throw new FormatException($"{name} must be a number")
                : null;
        }

        var duplicates = rows.GroupBy(row => (row.PostUrl, Second(row.CapturedAtUtc))).Where(group => group.Count() > 1);
        foreach (var duplicate in duplicates)
        {
            errors.Add($"Lines {string.Join(" and ", duplicate.Select(row => row.Line))}: the same post and capture time appear twice.");
        }

        return (rows, errors);
    }

    public static DateTimeOffset Second(DateTimeOffset value) =>
        new(value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerSecond), TimeSpan.Zero);

    private static decimal? Completion(string raw)
    {
        if (raw.Length == 0)
        {
            return null;
        }

        var percent = raw.EndsWith('%');
        if (!decimal.TryParse(raw.TrimEnd('%'), NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
        {
            throw new FormatException("completion_rate must be a number such as 0.42 or 42%");
        }

        return percent || value > 1 ? value / 100 : value;
    }

    /// <summary>Splits one CSV line, honoring double-quoted cells.</summary>
    private static List<string> Split(string line)
    {
        var cells = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (quoted)
            {
                if (character == '"' && index + 1 < line.Length && line[index + 1] == '"')
                {
                    current.Append('"');
                    index++;
                }
                else if (character == '"')
                {
                    quoted = false;
                }
                else
                {
                    current.Append(character);
                }
            }
            else if (character == '"')
            {
                quoted = true;
            }
            else if (character == ',')
            {
                cells.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(character);
            }
        }

        cells.Add(current.ToString());
        return cells;
    }
}
