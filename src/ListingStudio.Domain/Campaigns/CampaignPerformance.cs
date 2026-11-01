namespace ListingStudio.Domain.Campaigns;

/// <summary>Where a vertical library video was posted. Stored by name; append new values only.</summary>
public enum DistributionPlatform
{
    TikTok,
    InstagramReels,
    YouTubeShorts,
}

/// <summary>How a metric snapshot arrived. Stored by name; append new values only.</summary>
public enum CampaignMetricSource
{
    Manual,
    CsvImport,
}

/// <summary>
/// One posting of one vertical library video: the platform, the public post URL the agent pasted, and when it went
/// live. Entered by hand or matched by CSV import; nothing here logs in to, scrapes or publishes to a platform.
/// </summary>
public sealed class CampaignDistributionRecord
{
    public const int MaximumPostUrlLength = 500;

    private CampaignDistributionRecord() { }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid PropertyId { get; private set; }

    public Guid CampaignGenerationJobId { get; private set; }

    public Guid CampaignVideoVariantId { get; private set; }

    public Guid CampaignDeliverableId { get; private set; }

    public DistributionPlatform Platform { get; private set; }

    public string PostUrl { get; private set; } = string.Empty;

    public DateTimeOffset PublishedAtUtc { get; private set; }

    public string CreatedByUserId { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static CampaignDistributionRecord Create(
        Guid organizationId,
        Guid propertyId,
        Guid jobId,
        Guid variantId,
        Guid deliverableId,
        DistributionPlatform platform,
        string postUrl,
        DateTimeOffset publishedAtUtc,
        string userId,
        DateTimeOffset now)
    {
        if (organizationId == Guid.Empty || propertyId == Guid.Empty || jobId == Guid.Empty || variantId == Guid.Empty
            || deliverableId == Guid.Empty)
        {
            throw new ArgumentException("A workspace, property, campaign and video are required.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        if (!Enum.IsDefined(platform))
        {
            throw new ArgumentOutOfRangeException(nameof(platform));
        }

        if (publishedAtUtc > now.AddMinutes(5))
        {
            throw new ArgumentException("The publication time can't be in the future.", nameof(publishedAtUtc));
        }

        return new CampaignDistributionRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            PropertyId = propertyId,
            CampaignGenerationJobId = jobId,
            CampaignVideoVariantId = variantId,
            CampaignDeliverableId = deliverableId,
            Platform = platform,
            PostUrl = NormalizePostUrl(platform, postUrl),
            PublishedAtUtc = publishedAtUtc.ToUniversalTime(),
            CreatedByUserId = userId,
            CreatedAtUtc = now,
        };
    }

    /// <summary>An https link on the platform's own site, without its query string or fragment (tracking parameters).</summary>
    public static string NormalizePostUrl(DistributionPlatform platform, string? postUrl)
    {
        if (string.IsNullOrWhiteSpace(postUrl) || postUrl.Trim().Length > MaximumPostUrlLength
            || !Uri.TryCreate(postUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Paste the post's full https link.", nameof(postUrl));
        }

        var host = uri.Host.ToLowerInvariant();
        bool On(string domain) => host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);
        var matches = platform switch
        {
            DistributionPlatform.TikTok => On("tiktok.com"),
            DistributionPlatform.InstagramReels => On("instagram.com"),
            DistributionPlatform.YouTubeShorts => On("youtube.com") || host == "youtu.be",
            _ => false,
        };
        if (!matches)
        {
            throw new ArgumentException($"That link isn't a {platform switch { DistributionPlatform.InstagramReels => "Instagram", DistributionPlatform.YouTubeShorts => "YouTube", _ => "TikTok" }} post.", nameof(postUrl));
        }

        return $"https://{host}{uri.AbsolutePath.TrimEnd('/')}";
    }
}

/// <summary>The numbers one snapshot reports. Optional values are null when the platform doesn't show them.</summary>
public sealed record CampaignMetricValues(
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
    public void Validate()
    {
        if (Views < 0 || Likes < 0 || Comments < 0 || Shares < 0 || Saves < 0 || Clicks < 0 || Leads < 0 || BookedShowings < 0)
        {
            throw new ArgumentException("Numbers can't be negative.");
        }

        if (Likes > Views || Comments > Views || Shares > Views || Saves > Views || Clicks > Views)
        {
            throw new ArgumentException("Likes, comments, shares, saves and clicks can't be more than views.");
        }

        if (AverageWatchSeconds is < 0 or > 3_600)
        {
            throw new ArgumentException("Average watch time must be between 0 and 3,600 seconds.");
        }

        if (CompletionRate is < 0 or > 1)
        {
            throw new ArgumentException("Completion rate must be between 0% and 100%.");
        }

        if (Views > 10_000_000_000)
        {
            throw new ArgumentException("That view count is too large.");
        }
    }
}

/// <summary>
/// An append-only reading of one post's numbers at one moment. A newer reading never overwrites an older one (the
/// database rejects updates and deletes); calculations use the latest reading per post and keep the raw values.
/// </summary>
public sealed class CampaignMetricSnapshot
{
    private CampaignMetricSnapshot() { }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid DistributionRecordId { get; private set; }

    public long Views { get; private set; }

    public long Likes { get; private set; }

    public long Comments { get; private set; }

    public long Shares { get; private set; }

    public long Saves { get; private set; }

    public decimal? AverageWatchSeconds { get; private set; }

    public decimal? CompletionRate { get; private set; }

    public long Clicks { get; private set; }

    public int Leads { get; private set; }

    public int BookedShowings { get; private set; }

    public DateTimeOffset CapturedAtUtc { get; private set; }

    public CampaignMetricSource Source { get; private set; }

    public string EnteredByUserId { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public CampaignMetricValues Values => new(Views, Likes, Comments, Shares, Saves, AverageWatchSeconds, CompletionRate, Clicks, Leads, BookedShowings);

    public static CampaignMetricSnapshot Create(
        CampaignDistributionRecord record,
        CampaignMetricValues values,
        DateTimeOffset capturedAtUtc,
        CampaignMetricSource source,
        string userId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        values.Validate();
        if (!Enum.IsDefined(source))
        {
            throw new ArgumentOutOfRangeException(nameof(source));
        }

        var captured = capturedAtUtc.ToUniversalTime();
        if (captured > now.AddMinutes(5) || captured < record.PublishedAtUtc)
        {
            throw new ArgumentException("The capture time must be after the post went live and not in the future.", nameof(capturedAtUtc));
        }

        return new CampaignMetricSnapshot
        {
            Id = Guid.NewGuid(),
            OrganizationId = record.OrganizationId,
            DistributionRecordId = record.Id,
            Views = values.Views,
            Likes = values.Likes,
            Comments = values.Comments,
            Shares = values.Shares,
            Saves = values.Saves,
            AverageWatchSeconds = values.AverageWatchSeconds,
            CompletionRate = values.CompletionRate,
            Clicks = values.Clicks,
            Leads = values.Leads,
            BookedShowings = values.BookedShowings,
            // Stored to the second, so the same reading imported twice is recognized as a duplicate.
            CapturedAtUtc = new DateTimeOffset(captured.Ticks - (captured.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero),
            Source = source,
            EnteredByUserId = userId,
            CreatedAtUtc = now,
        };
    }
}

/// <summary>Stored by name; append new values only.</summary>
public enum VerticalRuleReportStatus
{
    Draft,
    Promoted,
}

/// <summary>
/// A draft evaluation of the recommendation rules against measured results, segmented by format, property type,
/// price band, media availability and agent. It never changes the rules: a rule version becomes active only by an
/// explicit administrator promotion of a report that meets the documented minimum sample.
/// </summary>
public sealed class VerticalRuleEvaluationReport
{
    private VerticalRuleEvaluationReport() { }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public string CreatedByUserId { get; private set; } = string.Empty;

    public string BaseRuleVersion { get; private set; } = string.Empty;

    public string CalculationVersion { get; private set; } = string.Empty;

    public int VideoCount { get; private set; }

    public long TotalViews { get; private set; }

    public bool MeetsMinimumSample { get; private set; }

    /// <summary>Segment counts and rates only; no property text, URLs or ids.</summary>
    public string SegmentsJson { get; private set; } = "[]";

    public VerticalRuleReportStatus Status { get; private set; }

    public string? PromotedRuleVersion { get; private set; }

    public string? PromotedByUserId { get; private set; }

    public DateTimeOffset? PromotedAtUtc { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static VerticalRuleEvaluationReport CreateDraft(Guid organizationId, string userId, string baseRuleVersion,
        string calculationVersion, int videoCount, long totalViews, bool meetsMinimumSample, string segmentsJson, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseRuleVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(calculationVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentsJson);
        if (organizationId == Guid.Empty || videoCount < 0 || totalViews < 0 || segmentsJson.Length > 200_000)
        {
            throw new ArgumentException("The report is not valid.");
        }

        return new VerticalRuleEvaluationReport
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            CreatedByUserId = userId,
            BaseRuleVersion = baseRuleVersion,
            CalculationVersion = calculationVersion,
            VideoCount = videoCount,
            TotalViews = totalViews,
            MeetsMinimumSample = meetsMinimumSample,
            SegmentsJson = segmentsJson,
            Status = VerticalRuleReportStatus.Draft,
            CreatedAtUtc = now,
        };
    }

    public void Promote(string ruleVersion, string userId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        if (Status != VerticalRuleReportStatus.Draft)
        {
            throw new InvalidOperationException("This report has already been used for a promotion.");
        }

        if (!MeetsMinimumSample)
        {
            throw new InvalidOperationException("This report doesn't meet the minimum sample, so no rule version can be promoted from it.");
        }

        Status = VerticalRuleReportStatus.Promoted;
        PromotedRuleVersion = ruleVersion;
        PromotedByUserId = userId;
        PromotedAtUtc = now;
    }
}
