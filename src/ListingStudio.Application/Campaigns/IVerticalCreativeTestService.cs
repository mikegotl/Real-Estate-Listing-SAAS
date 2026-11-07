using System.Text.Json;
using ListingStudio.Domain.Campaigns;

namespace ListingStudio.Application.Campaigns;

/// <summary>
/// The Creative Format Testing tab of the admin TikTok studio. Platform administrators only, scoped to their current
/// workspace, and only while VerticalVideoLibraryV1 is on for it: with the switch off every call is refused with
/// <see cref="CreativeTestingDisabledException"/>. Earlier suites, cases and videos stay stored either way.
/// </summary>
public interface IVerticalCreativeTestService
{
    /// <summary>True when the caller is a platform administrator and the switch is on for their workspace.</summary>
    Task<bool> IsEnabledAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Runs the recommendation engine on one property: every format's grounded score, reasons and hooks.</summary>
    Task<VerticalCreativeTestPreview?> PreviewAsync(string userId, Guid propertyId, CancellationToken cancellationToken = default);

    Task<VerticalCreativeTestSuiteView> StartAsync(string userId, Guid propertyId, VerticalCreativeTestMode mode,
        VerticalCreativeFormat? format, CampaignCaptionStyle captionStyle = CampaignCaptionStyles.Default,
        CancellationToken cancellationToken = default);

    Task CancelAsync(string userId, Guid suiteId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VerticalCreativeTestSuiteView>> ListAsync(string userId, CancellationToken cancellationToken = default);
}

/// <summary>Runs creative test cases one at a time in the background. Separate queue and lock from the render regression suite.</summary>
public interface IVerticalCreativeTestProcessor
{
    Task ProcessNextAsync(CancellationToken cancellationToken = default);
}

public sealed class CreativeTestingDisabledException()
    : InvalidOperationException("Creative format testing is switched off for this workspace.");

public sealed record VerticalCreativeTestPreview(
    Guid PropertyId,
    string Address,
    string RuleVersion,
    bool PhotoAnalysisPending,
    IReadOnlyList<VerticalCreativeTestFormatOption> Formats)
{
    public IReadOnlyList<VerticalCreativeTestFormatOption> Recommended =>
        Formats.Where(format => format.IsRecommended).OrderBy(format => format.Rank).ToArray();

    public IReadOnlyList<VerticalCreativeTestFormatOption> Eligible =>
        Formats.Where(format => format.IsEligible).OrderBy(format => format.Rank).ToArray();
}

public sealed record VerticalCreativeTestFormatOption(
    VerticalCreativeFormat Format,
    VerticalCampaignPurpose Purpose,
    int Score,
    VerticalRecommendationStrength Strength,
    bool IsEligible,
    bool IsRecommended,
    int Rank,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> MissingInputs,
    string? Hook,
    string? HookB,
    int DurationSeconds,
    string RecipeVersion);

public sealed record VerticalCreativeTestSuiteView(
    Guid Id,
    Guid PropertyId,
    string Address,
    VerticalCreativeTestMode Mode,
    VerticalCreativeFormat? Format,
    string RuleVersion,
    VerticalCreativeTestSuiteStatus Status,
    string? Error,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    IReadOnlyList<VerticalCreativeTestCaseView> Cases,
    CampaignCaptionStyle CaptionStyle = CampaignCaptionStyles.Default)
{
    public bool IsActive => Status is VerticalCreativeTestSuiteStatus.Queued or VerticalCreativeTestSuiteStatus.Running;
}

public sealed record VerticalCreativeTestCaseView(
    Guid Id,
    int Index,
    VerticalCreativeFormat Format,
    string HookVariant,
    VerticalCampaignPurpose Purpose,
    int Score,
    IReadOnlyList<string> Reasons,
    string RecipeVersion,
    int ExpectedDurationSeconds,
    VerticalCreativeTestCaseStatus Status,
    string? Error,
    Guid? JobId,
    Guid? VariantId,
    Guid? DeliverableId,
    string? Hook,
    int Version,
    CampaignVideoReviewDecision? Decision,
    IReadOnlyList<CampaignReviewEntry> History,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc)
{
    public bool CanReview => Status == VerticalCreativeTestCaseStatus.Passed && DeliverableId is not null && Version > 0;
}

/// <summary>Counts for the creative tab's dashboard. Published-with-metrics needs performance data (added later); 0 until then.</summary>
public sealed record VerticalCreativeTestSummary(int AwaitingReview, int TechnicalFailures, int ChangesRequested, int PublishedWithMetrics)
{
    public static VerticalCreativeTestSummary From(IEnumerable<VerticalCreativeTestSuiteView> suites, int publishedWithMetrics = 0)
    {
        var cases = suites.SelectMany(suite => suite.Cases).ToArray();
        return new VerticalCreativeTestSummary(
            cases.Count(item => item.CanReview && item.Decision is null),
            cases.Count(item => item.Status == VerticalCreativeTestCaseStatus.Failed),
            cases.Count(item => item.CanReview && item.Decision == CampaignVideoReviewDecision.ChangesRequested),
            publishedWithMetrics);
    }
}

/// <summary>Turns the recommendation for one property into the cases a creative test will run.</summary>
public static class VerticalCreativeTestPlanner
{
    private static readonly JsonSerializerOptions ReasonsOptions = new(JsonSerializerDefaults.Web);

    public static VerticalCreativeTestPreview Preview(Guid propertyId, string address, VerticalCreativePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var options = VerticalFormatRules.Formats
            .Select(format => plan.Recommendation.For(format))
            .OrderBy(item => item.Rank)
            .Select(item =>
            {
                var recipe = plan.Recipes.FirstOrDefault(candidate => candidate.Format == item.Format);
                var hookB = recipe is null ? null : VerticalHookVariants.ApplyB(recipe)?.Hook.Text;
                return new VerticalCreativeTestFormatOption(item.Format, item.Purpose, item.Score, item.Strength, item.IsEligible,
                    item.IsRecommended, item.Rank, item.Reasons, item.MissingInputs, recipe?.Hook.Text, hookB,
                    recipe?.DurationSeconds ?? DefaultDurationSeconds(item.Format), VerticalRecipePlanner.For(item.Format).RecipeVersion);
            })
            .ToArray();
        return new VerticalCreativeTestPreview(propertyId, address, plan.Recommendation.RuleVersion,
            plan.Input.Photos.Any(photo => !photo.AnalysisCompleted), options);
    }

    /// <summary>
    /// The cases for a mode. Formats the listing can't support become Skipped cases (with what's missing), never
    /// failures; a hook B that can't be worded safely is Skipped too.
    /// </summary>
    public static IReadOnlyList<VerticalCreativeTestCasePlan> Plan(
        VerticalCreativeTestPreview preview,
        VerticalCreativeTestMode mode,
        VerticalCreativeFormat? format)
    {
        ArgumentNullException.ThrowIfNull(preview);
        VerticalCreativeTestFormatOption Pick() => preview.Formats.FirstOrDefault(option => option.Format == format)
            ?? throw new ArgumentException("Choose a vertical video format.", nameof(format));
        return mode switch
        {
            VerticalCreativeTestMode.RecommendedThree => preview.Recommended.Count == 0
                ? throw new InvalidOperationException("No format is strong enough to recommend for this property yet.")
                : preview.Recommended.Select(option => Case(option, VerticalHookVariants.A)).ToArray(),
            VerticalCreativeTestMode.AllEligible => preview.Formats.OrderBy(option => option.Rank)
                .Select(option => Case(option, VerticalHookVariants.A)).ToArray(),
            VerticalCreativeTestMode.SingleFormat => [Case(Pick(), VerticalHookVariants.A, preview.PhotoAnalysisPending)],
            VerticalCreativeTestMode.HookAb =>
                [Case(Pick(), VerticalHookVariants.A, preview.PhotoAnalysisPending), Case(Pick(), VerticalHookVariants.B, preview.PhotoAnalysisPending)],
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
    }

    /// <summary>How many campaigns a mode will actually generate (skipped cases cost nothing).</summary>
    public static int GeneratedCount(IReadOnlyList<VerticalCreativeTestCasePlan> cases) => cases.Count(item => item.SkipReason is null);

    public static int DefaultDurationSeconds(VerticalCreativeFormat format) =>
        format is VerticalCreativeFormat.WowFeature or VerticalCreativeFormat.SpeedTour ? 15 : 30;

    public static IReadOnlyList<string> ReadReasons(string? json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<string[]>(json, ReasonsOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// A picked photo-led format is not skipped while photo analysis is still running: it may become eligible, and is
    /// checked again (with its hook B) when its case starts.
    /// </summary>
    public static bool AwaitsAnalysis(VerticalCreativeTestFormatOption option, bool analysisPending) =>
        analysisPending && !option.IsEligible && option.Format is VerticalCreativeFormat.WowFeature or VerticalCreativeFormat.PriceFirst;

    private static VerticalCreativeTestCasePlan Case(VerticalCreativeTestFormatOption option, string hookVariant, bool analysisPending = false)
    {
        var skip = AwaitsAnalysis(option, analysisPending)
            ? null
            : !option.IsEligible
            ? $"This listing can't support {VerticalFormatNames.Name(option.Format)} yet"
                + (option.MissingInputs.Count > 0 ? $": {string.Join("; ", option.MissingInputs)}" : ".")
            : hookVariant == VerticalHookVariants.B && option.HookB is null
                ? $"{VerticalFormatNames.Name(option.Format)} has no hook B that keeps to the verified facts for this listing."
                : null;
        var reasons = JsonSerializer.Serialize(option.Reasons.Take(12).ToArray(), ReasonsOptions);
        if (reasons.Length > VerticalCreativeTestCase.MaximumReasonsJsonLength)
        {
            reasons = "[]";
        }

        return new VerticalCreativeTestCasePlan(option.Format, hookVariant, option.Score, reasons, option.RecipeVersion,
            option.DurationSeconds, skip);
    }
}
