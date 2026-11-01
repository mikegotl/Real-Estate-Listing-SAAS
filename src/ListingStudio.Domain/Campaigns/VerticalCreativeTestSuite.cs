namespace ListingStudio.Domain.Campaigns;

/// <summary>What an administrator asked a creative test to make. Stored by name; append new values only.</summary>
public enum VerticalCreativeTestMode
{
    RecommendedThree,
    AllEligible,
    SingleFormat,
    HookAb,
}

/// <summary>Stored by name; append new values only.</summary>
public enum VerticalCreativeTestSuiteStatus
{
    Queued,
    Running,
    Completed,
    Cancelled,
}

/// <summary>Stored by name; append new values only.</summary>
public enum VerticalCreativeTestCaseStatus
{
    Pending,
    Running,
    Passed,
    Failed,
    Skipped,
    Cancelled,
}

/// <summary>
/// A platform administrator's creative format test on one property: one case per video to make, run one at a time
/// in the background. Each case is an ordinary private campaign that makes exactly one vertical library video, so a
/// case can pass, fail or be skipped without touching the others. Kept apart from the render regression suite
/// (<see cref="TikTokTestSuite"/>), whose fixed three cases test the renderer, not the creative formats.
/// </summary>
public sealed class VerticalCreativeTestSuite
{
    public const int MaximumErrorLength = 500;

    private VerticalCreativeTestSuite() { }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid PropertyId { get; private set; }

    public string RequestedByUserId { get; private set; } = string.Empty;

    public VerticalCreativeTestMode Mode { get; private set; }

    /// <summary>The format picked for <see cref="VerticalCreativeTestMode.SingleFormat"/> and <see cref="VerticalCreativeTestMode.HookAb"/>.</summary>
    public VerticalCreativeFormat? Format { get; private set; }

    /// <summary>The recommendation rule version the cases were scored with.</summary>
    public string RuleVersion { get; private set; } = string.Empty;

    public VerticalCreativeTestSuiteStatus Status { get; private set; }

    /// <summary>The caption look every test video uses. Stored by name.</summary>
    public CampaignCaptionStyle CaptionStyle { get; private set; } = CampaignCaptionStyles.Default;

    public string? Error { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public List<VerticalCreativeTestCase> Cases { get; private set; } = [];

    public static VerticalCreativeTestSuite Create(
        Guid organizationId,
        Guid propertyId,
        string userId,
        VerticalCreativeTestMode mode,
        VerticalCreativeFormat? format,
        string ruleVersion,
        IReadOnlyList<VerticalCreativeTestCasePlan> cases,
        DateTimeOffset now,
        CampaignCaptionStyle captionStyle = CampaignCaptionStyles.Default)
    {
        if (!Enum.IsDefined(captionStyle))
        {
            throw new ArgumentOutOfRangeException(nameof(captionStyle));
        }

        if (organizationId == Guid.Empty || propertyId == Guid.Empty)
        {
            throw new ArgumentException("A workspace and property are required.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleVersion);
        ArgumentNullException.ThrowIfNull(cases);
        if (!Enum.IsDefined(mode) || (format is { } chosen && !Enum.IsDefined(chosen)))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        if ((mode is VerticalCreativeTestMode.SingleFormat or VerticalCreativeTestMode.HookAb) != format.HasValue)
        {
            throw new ArgumentException("Pick one format for a single-format or hook A/B test, and none otherwise.", nameof(format));
        }

        if (cases.Count is 0 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(cases), "A creative test makes between 1 and 10 videos.");
        }

        var suite = new VerticalCreativeTestSuite
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            PropertyId = propertyId,
            RequestedByUserId = userId,
            Mode = mode,
            Format = format,
            RuleVersion = ruleVersion.Trim(),
            Status = VerticalCreativeTestSuiteStatus.Queued,
            CaptionStyle = captionStyle,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        for (var index = 0; index < cases.Count; index++)
        {
            suite.Cases.Add(VerticalCreativeTestCase.Create(suite, index, cases[index], now));
        }

        if (suite.Cases.Select(item => (item.Format, item.HookVariant)).Distinct().Count() != suite.Cases.Count)
        {
            throw new ArgumentException("Each case must be a different format or hook.", nameof(cases));
        }

        suite.CompleteIfDone(now);
        return suite;
    }

    public bool IsActive => Status is VerticalCreativeTestSuiteStatus.Queued or VerticalCreativeTestSuiteStatus.Running;

    /// <summary>The case being made now, if any.</summary>
    public VerticalCreativeTestCase? RunningCase =>
        Cases.Where(item => item.Status == VerticalCreativeTestCaseStatus.Running).OrderBy(item => item.Index).FirstOrDefault();

    /// <summary>The next case to start; cases run strictly one after another in index order.</summary>
    public VerticalCreativeTestCase? NextCase =>
        RunningCase is null
            ? Cases.Where(item => item.Status == VerticalCreativeTestCaseStatus.Pending).OrderBy(item => item.Index).FirstOrDefault()
            : null;

    public void MarkRunning(DateTimeOffset now)
    {
        if (Status == VerticalCreativeTestSuiteStatus.Queued)
        {
            Status = VerticalCreativeTestSuiteStatus.Running;
        }

        UpdatedAtUtc = now;
    }

    /// <summary>Completes the suite once no case is waiting or running. Failed and skipped cases still count as done.</summary>
    public void CompleteIfDone(DateTimeOffset now)
    {
        if (IsActive && Cases.All(item => VerticalCreativeTestCase.IsTerminal(item.Status)))
        {
            Status = VerticalCreativeTestSuiteStatus.Completed;
            CompletedAtUtc = now;
        }

        UpdatedAtUtc = now;
    }

    /// <summary>Stops the cases that have not finished. Finished cases and their videos are kept.</summary>
    public void Cancel(string? reason, DateTimeOffset now)
    {
        if (!IsActive)
        {
            return;
        }

        foreach (var item in Cases.Where(item => !VerticalCreativeTestCase.IsTerminal(item.Status)))
        {
            item.Cancel(now);
        }

        Status = VerticalCreativeTestSuiteStatus.Cancelled;
        Error = Trim(reason);
        CompletedAtUtc = now;
        UpdatedAtUtc = now;
    }

    internal static string? Trim(string? text) => text is { Length: > MaximumErrorLength } ? text[..MaximumErrorLength] : text;
}

/// <summary>What one case will make, decided when the test is started.</summary>
public sealed record VerticalCreativeTestCasePlan(
    VerticalCreativeFormat Format,
    string HookVariant,
    int Score,
    string ReasonsJson,
    string RecipeVersion,
    int ExpectedDurationSeconds,
    string? SkipReason = null);

/// <summary>One video of a creative test, with the score and reasons it was picked on and the exact deliverable it made.</summary>
public sealed class VerticalCreativeTestCase
{
    public const int MaximumReasonsJsonLength = 8_000;

    private VerticalCreativeTestCase() { }

    public Guid Id { get; private set; }

    public Guid SuiteId { get; private set; }

    public Guid OrganizationId { get; private set; }

    public int Index { get; private set; }

    /// <summary>Deterministic from the suite and index, so a retried start finds the campaign it already queued.</summary>
    public Guid CaseKey { get; private set; }

    public VerticalCreativeFormat Format { get; private set; }

    /// <summary>"a" (the format's own hook) or "b" (the alternate wording of the same grounded fact).</summary>
    public string HookVariant { get; private set; } = CampaignVideoVariant.DefaultHookVariant;

    public int Score { get; private set; }

    public string ReasonsJson { get; private set; } = "[]";

    public string RecipeVersion { get; private set; } = string.Empty;

    public int ExpectedDurationSeconds { get; private set; }

    public Guid? CampaignGenerationJobId { get; private set; }

    public Guid? CampaignVideoVariantId { get; private set; }

    public Guid? CampaignDeliverableId { get; private set; }

    public VerticalCreativeTestCaseStatus Status { get; private set; }

    public string? Error { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public DateTimeOffset? StartedAtUtc { get; private set; }

    public DateTimeOffset? FinishedAtUtc { get; private set; }

    public VerticalCreativeTestSuite Suite { get; private set; } = null!;

    internal static VerticalCreativeTestCase Create(
        VerticalCreativeTestSuite suite,
        int index,
        VerticalCreativeTestCasePlan plan,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!Enum.IsDefined(plan.Format) || plan.Format == VerticalCreativeFormat.LegacySingle)
        {
            throw new ArgumentOutOfRangeException(nameof(plan), "A creative test case makes one library format.");
        }

        if (plan.HookVariant is not ("a" or "b"))
        {
            throw new ArgumentException("The hook is A or B.", nameof(plan));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(plan.RecipeVersion);
        if (plan.RecipeVersion.Length > CampaignVideoVariant.MaximumRecipeVersionLength
            || plan.ReasonsJson is not { Length: > 0 and <= MaximumReasonsJsonLength }
            || plan.Score is < 0 or > 100
            || !CampaignVideoVariant.AllowedDurations.Contains(plan.ExpectedDurationSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(plan), "The case score, reasons, recipe version or length is not valid.");
        }

        var keyBytes = suite.Id.ToByteArray();
        keyBytes[14] ^= 0x5C;
        keyBytes[15] ^= (byte)(index + 1);
        var created = new VerticalCreativeTestCase
        {
            Id = Guid.NewGuid(),
            SuiteId = suite.Id,
            OrganizationId = suite.OrganizationId,
            Suite = suite,
            Index = index,
            CaseKey = new Guid(keyBytes),
            Format = plan.Format,
            HookVariant = plan.HookVariant,
            Score = plan.Score,
            ReasonsJson = plan.ReasonsJson,
            RecipeVersion = plan.RecipeVersion.Trim(),
            ExpectedDurationSeconds = plan.ExpectedDurationSeconds,
            Status = VerticalCreativeTestCaseStatus.Pending,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        if (plan.SkipReason is not null)
        {
            created.Skip(plan.SkipReason, now);
        }

        return created;
    }

    public static bool IsTerminal(VerticalCreativeTestCaseStatus status) => status is VerticalCreativeTestCaseStatus.Passed
        or VerticalCreativeTestCaseStatus.Failed or VerticalCreativeTestCaseStatus.Skipped or VerticalCreativeTestCaseStatus.Cancelled;

    public void Start(Guid jobId, DateTimeOffset now)
    {
        if (jobId == Guid.Empty || Status != VerticalCreativeTestCaseStatus.Pending)
        {
            throw new InvalidOperationException("Only a waiting case can start.");
        }

        CampaignGenerationJobId = jobId;
        Status = VerticalCreativeTestCaseStatus.Running;
        StartedAtUtc = now;
        UpdatedAtUtc = now;
    }

    public void Pass(Guid variantId, Guid deliverableId, DateTimeOffset now)
    {
        EnsureRunning();
        CampaignVideoVariantId = variantId;
        CampaignDeliverableId = deliverableId;
        Finish(VerticalCreativeTestCaseStatus.Passed, null, now);
    }

    public void Fail(string error, Guid? variantId, Guid? deliverableId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        if (Status is not (VerticalCreativeTestCaseStatus.Pending or VerticalCreativeTestCaseStatus.Running))
        {
            throw new InvalidOperationException("This case has already finished.");
        }

        CampaignVideoVariantId = variantId ?? CampaignVideoVariantId;
        CampaignDeliverableId = deliverableId ?? CampaignDeliverableId;
        Finish(VerticalCreativeTestCaseStatus.Failed, error, now);
    }

    /// <summary>The listing can't support this format (or a distinct hook B): not a failure of the renderer.</summary>
    public void Skip(string reason, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (Status is not (VerticalCreativeTestCaseStatus.Pending or VerticalCreativeTestCaseStatus.Running))
        {
            throw new InvalidOperationException("This case has already finished.");
        }

        Finish(VerticalCreativeTestCaseStatus.Skipped, reason, now);
    }

    internal void Cancel(DateTimeOffset now) => Finish(VerticalCreativeTestCaseStatus.Cancelled, null, now);

    private void EnsureRunning()
    {
        if (Status != VerticalCreativeTestCaseStatus.Running)
        {
            throw new InvalidOperationException("Only a running case can pass.");
        }
    }

    private void Finish(VerticalCreativeTestCaseStatus status, string? error, DateTimeOffset now)
    {
        Status = status;
        Error = VerticalCreativeTestSuite.Trim(error);
        FinishedAtUtc = now;
        UpdatedAtUtc = now;
    }
}
