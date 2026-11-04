namespace ListingStudio.Domain.Campaigns;

/// <summary>
/// How a vertical video tells the listing's story. Independent of <see cref="CampaignMarketingTemplate"/>, which is
/// the campaign's message (Just listed, Open house, Price drop). Values are stored by name; append new ones.
/// </summary>
public enum VerticalCreativeFormat
{
    /// <summary>The single shared vertical edit every campaign made before the library existed.</summary>
    LegacySingle,

    /// <summary>Opens on the most striking visual; a short loop built for reach.</summary>
    WowFeature,

    /// <summary>Leads with the verified price, city and key facts.</summary>
    PriceFirst,

    /// <summary>A first-person walk-through hosted by the agent or a consented presenter.</summary>
    HostedPov,

    /// <summary>A verified problem, the tension, then the result and payoff.</summary>
    StoryReveal,

    /// <summary>Verified private or timely access; only with confirmed access language.</summary>
    ExclusiveAccess,
}

/// <summary>What a vertical video is meant to achieve. Values are stored by name; append new ones.</summary>
public enum VerticalCampaignPurpose
{
    Reach,
    Engagement,
    Consideration,
    LeadGeneration,
}

/// <summary>Which vertical videos a campaign asks for. Values are stored by name; append new ones.</summary>
public enum VerticalVideoSelection
{
    /// <summary>One shared vertical video, as before the library.</summary>
    LegacySingle,

    /// <summary>The three strongest formats the listing can support.</summary>
    RecommendedThree,

    /// <summary>Every format the listing can support.</summary>
    AllEligible,

    /// <summary>
    /// The formats the agent picked from the eligible ones (a recommended video removed or swapped for another).
    /// The list is kept on the campaign in <c>CampaignGenerationJob.VerticalFormats</c>.
    /// </summary>
    AgentChosen,
}

/// <summary>
/// What the agent wants the campaign to achieve. Separate from <see cref="VerticalCreativeFormat"/> (how each video
/// tells the story) and from <see cref="VerticalCampaignPurpose"/> (what one video is for). Stored by name; append new ones.
/// </summary>
public enum VerticalCampaignGoal
{
    MaximizeReach,
    BuyerInterest,
    OpenHouse,
    NewListing,
    PriceChange,
}

public enum CampaignVideoLibraryStatus
{
    Planned,
    Generating,
    Completed,
    PartiallyCompleted,
    Failed,
    Cancelled,
}

public enum CampaignVideoVariantStatus
{
    Planned,
    Generating,
    Rendered,
    Failed,
    Skipped,
    Cancelled,
}

/// <summary>
/// The set of ready-to-post vertical videos one campaign produced for a listing. Owned by exactly one campaign; its
/// variants are the individual videos. Feature tables only: the campaign and its legacy columns never carry
/// library-only values.
/// </summary>
public sealed class CampaignVideoLibrary
{
    public const int MaximumRuleVersionLength = 40;

    private CampaignVideoLibrary() { }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid PropertyId { get; private set; }

    public Guid CampaignGenerationJobId { get; private set; }

    public VerticalVideoSelection Selection { get; private set; }

    public CampaignVideoLibraryStatus Status { get; private set; }

    /// <summary>The recommendation rules that chose the formats, or null for a backfilled legacy library.</summary>
    public string? RuleVersion { get; private set; }

    /// <summary>The recommendation exactly as it was when the library was planned, so later rules never rewrite it.</summary>
    public string? RecommendationSnapshotJson { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public List<CampaignVideoVariant> Variants { get; private set; } = [];

    public static CampaignVideoLibrary Create(
        Guid organizationId,
        Guid propertyId,
        Guid campaignGenerationJobId,
        VerticalVideoSelection selection,
        string? ruleVersion,
        string? recommendationSnapshotJson,
        DateTimeOffset now)
    {
        if (organizationId == Guid.Empty || propertyId == Guid.Empty || campaignGenerationJobId == Guid.Empty)
        {
            throw new ArgumentException("A workspace, property and campaign are required.");
        }

        if (!Enum.IsDefined(selection))
        {
            throw new ArgumentOutOfRangeException(nameof(selection));
        }

        if (selection != VerticalVideoSelection.LegacySingle && string.IsNullOrWhiteSpace(ruleVersion))
        {
            throw new ArgumentException("A recommended library needs the rule version that chose it.", nameof(ruleVersion));
        }

        if (ruleVersion is { Length: > MaximumRuleVersionLength })
        {
            throw new ArgumentOutOfRangeException(nameof(ruleVersion));
        }

        if (recommendationSnapshotJson is { Length: > 200_000 })
        {
            throw new ArgumentOutOfRangeException(nameof(recommendationSnapshotJson));
        }

        return new CampaignVideoLibrary
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            PropertyId = propertyId,
            CampaignGenerationJobId = campaignGenerationJobId,
            Selection = selection,
            Status = CampaignVideoLibraryStatus.Planned,
            RuleVersion = string.IsNullOrWhiteSpace(ruleVersion) ? null : ruleVersion.Trim(),
            RecommendationSnapshotJson = string.IsNullOrWhiteSpace(recommendationSnapshotJson)
                ? null
                : recommendationSnapshotJson,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
    }

    /// <summary>Plans one more video in the library. Format and sequence together must be unique.</summary>
    public CampaignVideoVariant AddVariant(CampaignVideoVariantPlan plan, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (Status is not (CampaignVideoLibraryStatus.Planned or CampaignVideoLibraryStatus.Generating))
        {
            throw new InvalidOperationException("A finished library cannot take new videos.");
        }

        if (Variants.Any(variant => variant.Format == plan.Format && variant.Sequence == plan.Sequence))
        {
            throw new InvalidOperationException("This library already has that format at that position.");
        }

        var key = CampaignVideoVariant.CreateIdempotencyKey(
            CampaignGenerationJobId, plan.Format, plan.Sequence, plan.RecipeVersion, plan.HookVariant);
        if (Variants.Any(variant => variant.IdempotencyKey == key))
        {
            throw new InvalidOperationException("This library already has that video.");
        }

        var variant = CampaignVideoVariant.Create(this, plan, now);
        Variants.Add(variant);
        UpdatedAtUtc = now;
        return variant;
    }

    public void MarkGenerating(DateTimeOffset now)
    {
        if (Status == CampaignVideoLibraryStatus.Generating)
        {
            return;
        }

        if (Status != CampaignVideoLibraryStatus.Planned)
        {
            throw new InvalidOperationException("Only a planned library can start generating.");
        }

        Status = CampaignVideoLibraryStatus.Generating;
        UpdatedAtUtc = now;
    }

    /// <summary>
    /// Settles the library from its videos: complete when every required video rendered, partial when some did,
    /// failed when none did. Skipped (ineligible) videos are not failures.
    /// </summary>
    public void Finish(DateTimeOffset now)
    {
        if (IsTerminal(Status))
        {
            return;
        }

        if (Variants.Any(variant => !CampaignVideoVariant.IsTerminal(variant.Status)))
        {
            throw new InvalidOperationException("Every video must finish before the library does.");
        }

        var required = Variants.Where(variant => variant.Status != CampaignVideoVariantStatus.Skipped).ToArray();
        var rendered = required.Count(variant => variant.Status == CampaignVideoVariantStatus.Rendered);
        Status = required.Length == 0 || rendered == 0
            ? CampaignVideoLibraryStatus.Failed
            : rendered == required.Length
                ? CampaignVideoLibraryStatus.Completed
                : CampaignVideoLibraryStatus.PartiallyCompleted;
        CompletedAtUtc = now;
        UpdatedAtUtc = now;
    }

    /// <summary>Stops the library: unfinished videos are cancelled, finished ones are kept.</summary>
    public void Cancel(DateTimeOffset now)
    {
        if (IsTerminal(Status))
        {
            return;
        }

        foreach (var variant in Variants.Where(variant => !CampaignVideoVariant.IsTerminal(variant.Status)))
        {
            variant.Cancel(now);
        }

        Status = CampaignVideoLibraryStatus.Cancelled;
        CompletedAtUtc = now;
        UpdatedAtUtc = now;
    }

    public static bool IsTerminal(CampaignVideoLibraryStatus status) => status is CampaignVideoLibraryStatus.Completed
        or CampaignVideoLibraryStatus.PartiallyCompleted
        or CampaignVideoLibraryStatus.Failed
        or CampaignVideoLibraryStatus.Cancelled;
}

/// <summary>What a planned vertical video is: its creative angle, opening, voice and closing.</summary>
public sealed record CampaignVideoVariantPlan(
    VerticalCreativeFormat Format,
    VerticalCampaignPurpose Purpose,
    int Sequence,
    int DurationSeconds,
    bool IsEligible,
    string? HookText,
    string? HookGroundingKey,
    Guid? PrimaryPropertyMediaId,
    CampaignVoiceoverMode VoiceoverMode,
    string? CallToAction,
    string RecipeJson,
    string RecipeVersion,
    string? CaptionsJson = null,
    string HookVariant = CampaignVideoVariant.DefaultHookVariant);

/// <summary>
/// One planned video in a library: one creative angle with its own hook, primary shot, voiceover and call to action.
/// The recipe is fixed when the video is planned so the same plan always renders the same edit.
/// </summary>
public sealed class CampaignVideoVariant
{
    public const int MaximumHookLength = 100;
    public const int MaximumCallToActionLength = 80;
    public const int MaximumGroundingKeyLength = 200;
    public const int MaximumRecipeVersionLength = 40;
    public const int MaximumIdempotencyKeyLength = 160;
    public const string DefaultHookVariant = "a";
    public static readonly IReadOnlySet<int> AllowedDurations = new HashSet<int> { 15, 30, 45, 60 };

    private CampaignVideoVariant() { }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid CampaignVideoLibraryId { get; private set; }

    /// <summary>
    /// <c>{job}:{format}:{sequence}:{recipe version}:{hook variant}</c>. The same plan always maps to the same video, so
    /// a retried or resumed campaign finds the work it already did instead of adding a duplicate. Null only for the
    /// backfilled legacy video.
    /// </summary>
    public string? IdempotencyKey { get; private set; }

    public string HookVariant { get; private set; } = DefaultHookVariant;

    public VerticalCreativeFormat Format { get; private set; }

    public VerticalCampaignPurpose Purpose { get; private set; }

    public int Sequence { get; private set; }

    public int DurationSeconds { get; private set; }

    /// <summary>Whether the listing could support this format when it was planned; ineligible videos are skipped.</summary>
    public bool IsEligible { get; private set; }

    public string? HookText { get; private set; }

    public string? HookGroundingKey { get; private set; }

    public Guid? PrimaryPropertyMediaId { get; private set; }

    public CampaignVoiceoverMode VoiceoverMode { get; private set; }

    public string? CallToAction { get; private set; }

    public string RecipeJson { get; private set; } = string.Empty;

    public string RecipeVersion { get; private set; } = string.Empty;

    /// <summary>This video's own social caption copy; the campaign-wide captions stay for legacy and landscape outputs.</summary>
    public string? CaptionsJson { get; private set; }

    /// <summary>This video's voiceover in the campaign's spoken language, kept so a retry never re-translates it.</summary>
    public string? NarrationLocalizationJson { get; private set; }

    public CampaignVideoVariantStatus Status { get; private set; }

    public string? Error { get; private set; }

    /// <summary>This video's own Kling 4 native-presenter request (Kling4NativePresenter campaigns only).</summary>
    public string? KlingProviderRequestId { get; private set; }

    public string? KlingGenerationVersion { get; private set; }

    public string? KlingSubmittedPrompt { get; private set; }

    public DateTimeOffset? KlingSubmittedAtUtc { get; private set; }

    /// <summary>The finished Kling 4 video, once Higgsfield returned it.</summary>
    public string? KlingAssetPath { get; private set; }

    public long? KlingFileSize { get; private set; }

    public int? KlingDurationMs { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public DateTimeOffset? StartedAtUtc { get; private set; }

    public DateTimeOffset? FinishedAtUtc { get; private set; }

    public CampaignVideoLibrary Library { get; private set; } = null!;

    /// <summary>The rendered video, once there is one.</summary>
    public CampaignDeliverable? Deliverable { get; private set; }

    internal static CampaignVideoVariant Create(CampaignVideoLibrary library, CampaignVideoVariantPlan plan, DateTimeOffset now)
    {
        if (!Enum.IsDefined(plan.Format) || !Enum.IsDefined(plan.Purpose) || !Enum.IsDefined(plan.VoiceoverMode))
        {
            throw new ArgumentOutOfRangeException(nameof(plan), "The format, purpose or voiceover is not supported.");
        }

        if (plan.Sequence is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(plan), "The video position must be between 1 and 50.");
        }

        if (!AllowedDurations.Contains(plan.DurationSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(plan), "A vertical video is 15, 30, 45 or 60 seconds long.");
        }

        var hook = Optional(plan.HookText, MaximumHookLength, "Keep the hook under 100 characters.");
        var hookKey = Optional(plan.HookGroundingKey, MaximumGroundingKeyLength, "The hook grounding key is too long.");
        if (hook is not null && hookKey is null)
        {
            throw new ArgumentException("A hook must name the verified fact it comes from.", nameof(plan));
        }

        var callToAction = Optional(plan.CallToAction, MaximumCallToActionLength, "Keep the call to action under 80 characters.");
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.RecipeJson);
        if (plan.RecipeJson.Length > 200_000 || plan.CaptionsJson is { Length: > 20_000 })
        {
            throw new ArgumentOutOfRangeException(nameof(plan), "The recipe or captions are too large.");
        }

        var recipeVersion = Optional(plan.RecipeVersion, MaximumRecipeVersionLength, "The recipe version is too long.")
            ?? throw new ArgumentException("A recipe version is required.", nameof(plan));
        return new CampaignVideoVariant
        {
            Id = Guid.NewGuid(),
            OrganizationId = library.OrganizationId,
            CampaignVideoLibraryId = library.Id,
            IdempotencyKey = CreateIdempotencyKey(
                library.CampaignGenerationJobId, plan.Format, plan.Sequence, recipeVersion, plan.HookVariant),
            HookVariant = NormalizeHookVariant(plan.HookVariant),
            Library = library,
            Format = plan.Format,
            Purpose = plan.Purpose,
            Sequence = plan.Sequence,
            DurationSeconds = plan.DurationSeconds,
            IsEligible = plan.IsEligible,
            HookText = hook,
            HookGroundingKey = hookKey,
            PrimaryPropertyMediaId = plan.PrimaryPropertyMediaId == Guid.Empty ? null : plan.PrimaryPropertyMediaId,
            VoiceoverMode = plan.VoiceoverMode,
            CallToAction = callToAction,
            RecipeJson = plan.RecipeJson,
            RecipeVersion = recipeVersion,
            CaptionsJson = string.IsNullOrWhiteSpace(plan.CaptionsJson) ? null : plan.CaptionsJson,
            Status = plan.IsEligible ? CampaignVideoVariantStatus.Planned : CampaignVideoVariantStatus.Skipped,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            FinishedAtUtc = plan.IsEligible ? null : now,
        };
    }

    public static string CreateIdempotencyKey(
        Guid campaignGenerationJobId,
        VerticalCreativeFormat format,
        int sequence,
        string recipeVersion,
        string? hookVariant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipeVersion);
        var key = $"{campaignGenerationJobId:N}:{format}:{sequence}:{recipeVersion.Trim()}:{NormalizeHookVariant(hookVariant)}";
        return key.Length > MaximumIdempotencyKeyLength ? throw new ArgumentException("The video key is too long.") : key;
    }

    private static string NormalizeHookVariant(string? hookVariant)
    {
        var normalized = string.IsNullOrWhiteSpace(hookVariant) ? DefaultHookVariant : hookVariant.Trim().ToLowerInvariant();
        return normalized.Length <= 8 && normalized.All(char.IsAsciiLetterOrDigit)
            ? normalized
            : throw new ArgumentException("A hook variant is a short letter or number code.", nameof(hookVariant));
    }

    /// <summary>This video's own post caption copy, written once its edit is fixed.</summary>
    public void SetCaptions(string captionsJson, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captionsJson);
        if (captionsJson.Length > 20_000)
        {
            throw new ArgumentOutOfRangeException(nameof(captionsJson));
        }

        CaptionsJson = captionsJson;
        UpdatedAtUtc = now;
    }

    public void SetNarrationLocalization(string localizationJson, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localizationJson);
        if (localizationJson.Length > 200_000)
        {
            throw new ArgumentOutOfRangeException(nameof(localizationJson));
        }

        NarrationLocalizationJson = localizationJson;
        UpdatedAtUtc = now;
    }

    public void MarkGenerating(DateTimeOffset now)
    {
        if (Status == CampaignVideoVariantStatus.Generating)
        {
            return;
        }

        if (Status != CampaignVideoVariantStatus.Planned)
        {
            throw new InvalidOperationException("Only a planned video can start generating.");
        }

        Status = CampaignVideoVariantStatus.Generating;
        StartedAtUtc ??= now;
        UpdatedAtUtc = now;
    }

    public void MarkRendered(DateTimeOffset now)
    {
        if (Status != CampaignVideoVariantStatus.Generating)
        {
            throw new InvalidOperationException("Only a video that is generating can finish rendering.");
        }

        Status = CampaignVideoVariantStatus.Rendered;
        Error = null;
        FinishedAtUtc = now;
        UpdatedAtUtc = now;
    }

    public void MarkFailed(string error, DateTimeOffset now)
    {
        if (IsTerminal(Status) && Status != CampaignVideoVariantStatus.Failed)
        {
            throw new InvalidOperationException("A finished video cannot fail.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        Status = CampaignVideoVariantStatus.Failed;
        Error = error.Length > 500 ? error[..500] : error;
        FinishedAtUtc = now;
        UpdatedAtUtc = now;
    }

    /// <summary>Puts a failed video back in the queue; rendered, skipped and cancelled videos stay as they are.</summary>
    public void Retry(DateTimeOffset now)
    {
        if (Status != CampaignVideoVariantStatus.Failed)
        {
            throw new InvalidOperationException("Only a failed video can be retried.");
        }

        Status = CampaignVideoVariantStatus.Planned;
        FinishedAtUtc = null;
        UpdatedAtUtc = now;
    }

    public void MarkKlingSubmitted(string requestId, string generationVersion, string submittedPrompt, DateTimeOffset now)
    {
        if (IsTerminal(Status))
        {
            throw new InvalidOperationException("A finished video cannot start a Kling request.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(generationVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(submittedPrompt);
        if (requestId.Length > 200 || generationVersion.Length > 200 || submittedPrompt.Length > 20_000)
        {
            throw new ArgumentOutOfRangeException(nameof(requestId), "The Kling request details are too long.");
        }

        KlingProviderRequestId = requestId.Trim();
        KlingGenerationVersion = generationVersion.Trim();
        KlingSubmittedPrompt = submittedPrompt;
        KlingSubmittedAtUtc = now;
        KlingAssetPath = null;
        KlingFileSize = null;
        KlingDurationMs = null;
        UpdatedAtUtc = now;
    }

    public void MarkKlingCompleted(string assetPath, long fileSize, int durationMs, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(KlingProviderRequestId))
        {
            throw new InvalidOperationException("No Kling request was submitted for this video.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(assetPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fileSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(durationMs);
        if (assetPath.Length > 1_024)
        {
            throw new ArgumentOutOfRangeException(nameof(assetPath));
        }

        KlingAssetPath = assetPath;
        KlingFileSize = fileSize;
        KlingDurationMs = durationMs;
        UpdatedAtUtc = now;
    }

    /// <summary>Forgets a failed or timed-out Kling request, so a retry of this video submits a new one.</summary>
    public void ResetKling(DateTimeOffset now)
    {
        KlingProviderRequestId = null;
        KlingSubmittedAtUtc = null;
        KlingAssetPath = null;
        KlingFileSize = null;
        KlingDurationMs = null;
        UpdatedAtUtc = now;
    }

    public void Cancel(DateTimeOffset now)
    {
        if (IsTerminal(Status))
        {
            return;
        }

        Status = CampaignVideoVariantStatus.Cancelled;
        FinishedAtUtc = now;
        UpdatedAtUtc = now;
    }

    public static bool IsTerminal(CampaignVideoVariantStatus status) => status is CampaignVideoVariantStatus.Rendered
        or CampaignVideoVariantStatus.Failed
        or CampaignVideoVariantStatus.Skipped
        or CampaignVideoVariantStatus.Cancelled;

    private static string? Optional(string? value, int maximumLength, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length > maximumLength ? throw new ArgumentException(message) : normalized;
    }
}
