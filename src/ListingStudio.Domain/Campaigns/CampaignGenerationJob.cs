using ListingStudio.Domain.Organizations;
using ListingStudio.Domain.Properties;
using ListingStudio.Domain.Videos;

namespace ListingStudio.Domain.Campaigns;

public enum CampaignGenerationStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>The visual editing strategy selected before campaign work begins.</summary>
public enum CampaignVideoStrategy
{
    Established = 0,
    Kling4FullTour = 1,
}

public enum CampaignGenerationStage
{
    ValidateProperty,
    AnalyzeMedia,
    GenerateStory,
    GenerateMasterVideoPlan,
    GenerateNarration,
    GenerateDerivativePlans,
    GenerateRequiredAiVideo,
    RenderHero,
    RenderFeature,
    RenderTeaser,
    RenderLongForm,
    GenerateSocialCopy,
    FinalizeCampaign,

    // Vertical video library (VerticalVideoLibraryV1). Appended; stored by name.
    PlanVerticalLibrary,
    GenerateVariantNarration,
    RenderVariants,
    GenerateVariantCopy,
    FinalizeVerticalLibrary,
}

/// <summary>
/// The order stages run in. Persisted values are append-only, so the library stages have higher numbers than the
/// stages they run between; compare stages with <see cref="Rank"/>, never with their numeric values.
/// </summary>
public static class CampaignGenerationStageOrder
{
    private static readonly CampaignGenerationStage[] Ordered =
    [
        CampaignGenerationStage.ValidateProperty,
        CampaignGenerationStage.AnalyzeMedia,
        CampaignGenerationStage.GenerateStory,
        CampaignGenerationStage.GenerateMasterVideoPlan,
        CampaignGenerationStage.GenerateNarration,
        CampaignGenerationStage.PlanVerticalLibrary,
        CampaignGenerationStage.GenerateDerivativePlans,
        CampaignGenerationStage.GenerateRequiredAiVideo,
        CampaignGenerationStage.RenderHero,
        CampaignGenerationStage.RenderFeature,
        CampaignGenerationStage.RenderTeaser,
        CampaignGenerationStage.RenderLongForm,
        CampaignGenerationStage.GenerateVariantNarration,
        CampaignGenerationStage.RenderVariants,
        CampaignGenerationStage.GenerateVariantCopy,
        CampaignGenerationStage.FinalizeVerticalLibrary,
        CampaignGenerationStage.GenerateSocialCopy,
        CampaignGenerationStage.FinalizeCampaign,
    ];

    public static int Rank(CampaignGenerationStage stage)
    {
        var index = Array.IndexOf(Ordered, stage);
        return index >= 0 ? index : throw new ArgumentOutOfRangeException(nameof(stage));
    }

    public static bool IsVerticalLibraryStage(CampaignGenerationStage stage) => stage is CampaignGenerationStage.PlanVerticalLibrary
        or CampaignGenerationStage.GenerateVariantNarration
        or CampaignGenerationStage.RenderVariants
        or CampaignGenerationStage.GenerateVariantCopy
        or CampaignGenerationStage.FinalizeVerticalLibrary;

    /// <summary>The stages a job shows, in running order; library stages only for library jobs.</summary>
    public static IReadOnlyList<CampaignGenerationStage> For(bool usesVerticalLibrary, CampaignGenerationStage current) =>
        Ordered.Where(stage => usesVerticalLibrary || !IsVerticalLibraryStage(stage) || stage == current).ToArray();
}

public sealed class CampaignGenerationJob
{
    private CampaignGenerationJob()
    {
    }

    private CampaignGenerationJob(
        Guid organizationId,
        Guid propertyId,
        string requestedByUserId,
        string sourceFingerprint,
        CampaignOutputSelection requestedOutputs,
        CampaignCreativeOptions creativeOptions,
        CampaignNarrationPreferences narrationPreferences,
        DateTimeOffset now)
    {
        Id = Guid.NewGuid();
        OrganizationId = organizationId;
        PropertyId = propertyId;
        RequestedByUserId = Required(requestedByUserId, 450, nameof(requestedByUserId));
        SourceFingerprint = Required(sourceFingerprint, 64, nameof(sourceFingerprint));
        CampaignOutputSelectionPolicy.Validate(requestedOutputs);
        creativeOptions.Validate();
        narrationPreferences.Validate();
        RequestedOutputs = requestedOutputs;
        CreativeElements = creativeOptions.Elements;
        Soundtrack = creativeOptions.Soundtrack;
        CaptionStyle = creativeOptions.CaptionStyle;
        AgentPortraitId = creativeOptions.AgentPortraitId;
        MarketingTemplate = creativeOptions.MarketingTemplate;
        PriceReductionAmount = creativeOptions.PriceReductionAmount;
        PriceReductionVerified = creativeOptions.PriceReductionVerified;
        AgentAvatarConsentConfirmed = creativeOptions.AgentAvatarConsentConfirmed;
        Pace = creativeOptions.Pace;
        ShortFormVoiceover = creativeOptions.Voiceover;
        OpenHouseText = creativeOptions.OpenHouseText?.Trim();
        OpenHouseVerified = creativeOptions.OpenHouseVerified;
        NarrationRegion = narrationPreferences.Region;
        SpokenLanguage = narrationPreferences.SpokenLanguage;
        CaptionLanguage = narrationPreferences.CaptionLanguage;
        VoiceAccent = narrationPreferences.VoiceAccent;
        LocalizeAgentCard = narrationPreferences.LocalizeAgentCard;
        Status = CampaignGenerationStatus.Queued;
        CurrentStage = CampaignGenerationStage.ValidateProperty;
        NextAttemptAtUtc = now;
        CreatedAtUtc = now;
        UpdatedAtUtc = now;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid PropertyId { get; private set; }

    public string RequestedByUserId { get; private set; } = string.Empty;

    public string SourceFingerprint { get; private set; } = string.Empty;

    public CampaignOutputSelection RequestedOutputs { get; private set; }

    /// <summary>
    /// Established is the control. Kling4FullTour is only retained while its release switch remains enabled.
    /// </summary>
    public CampaignVideoStrategy VideoStrategy { get; private set; }

    public string? FullTourProviderRequestId { get; private set; }

    public string? FullTourGenerationVersion { get; private set; }

    public DateTimeOffset? FullTourSubmittedAtUtc { get; private set; }

    public string? FullTourAssetPath { get; private set; }

    public string? FullTourContentType { get; private set; }

    public long? FullTourFileSize { get; private set; }

    public int? FullTourDurationMs { get; private set; }

    public CampaignVideoElement CreativeElements { get; private set; }

    public CampaignSoundtrack Soundtrack { get; private set; }

    public CampaignCaptionStyle CaptionStyle { get; private set; }

    public Guid? AgentPortraitId { get; private set; }

    public CampaignMarketingTemplate MarketingTemplate { get; private set; }

    public decimal? PriceReductionAmount { get; private set; }

    public bool PriceReductionVerified { get; private set; }

    public bool AgentAvatarConsentConfirmed { get; private set; }

    public CampaignPace Pace { get; private set; }

    public CampaignVoiceoverMode ShortFormVoiceover { get; private set; }

    public string? OpenHouseText { get; private set; }

    public bool OpenHouseVerified { get; private set; }

    public string? ShortFormLocalizedCopyJson { get; private set; }

    /// <summary>
    /// Null for every campaign made without the vertical video library. Set only when the library was on for the
    /// workspace when the campaign was requested; cleared again if the library falls back to one shared video.
    /// </summary>
    public VerticalVideoSelection? VerticalSelection { get; private set; }

    /// <summary>The campaign makes several recommended vertical videos instead of one shared vertical edit.</summary>
    public bool UsesVerticalLibrary => VerticalSelection is VerticalVideoSelection.RecommendedThree
        or VerticalVideoSelection.AllEligible or VerticalVideoSelection.AgentChosen;

    /// <summary>The agent's goal for a library campaign; null for legacy campaigns and when none was chosen.</summary>
    public VerticalCampaignGoal? VerticalGoal { get; private set; }

    /// <summary>Comma-separated format names, in the agent's order; only with <see cref="VerticalVideoSelection.AgentChosen"/>.</summary>
    public string? VerticalFormatsValue { get; private set; }

    /// <summary>The formats the agent picked, in order; empty unless the selection is <see cref="VerticalVideoSelection.AgentChosen"/>.</summary>
    public IReadOnlyList<VerticalCreativeFormat> VerticalFormats => ParseVerticalFormats(VerticalFormatsValue);

    /// <summary>The most formats one campaign can ask for: every library format once.</summary>
    public const int MaximumVerticalFormats = 5;

    /// <summary>
    /// "b" asks for the alternate hook wording of the one chosen format (admin creative A/B tests only); null means the
    /// format's own hook. Only with an agent-chosen list of exactly one format.
    /// </summary>
    public string? VerticalHookVariant { get; private set; }

    public CampaignNarrationRegion NarrationRegion { get; private set; }

    public CampaignLanguage SpokenLanguage { get; private set; }

    public CampaignLanguage CaptionLanguage { get; private set; }

    public CampaignVoiceAccent VoiceAccent { get; private set; }

    public bool LocalizeAgentCard { get; private set; }

    public string? LocalizedCopyJson { get; private set; }

    public string? LongFormLocalizedCopyJson { get; private set; }

    public CampaignGenerationStatus Status { get; private set; }

    public CampaignGenerationStage CurrentStage { get; private set; }

    public int StageAttemptCount { get; private set; }

    public int TotalStageAttempts { get; private set; }

    public DateTimeOffset? NextAttemptAtUtc { get; private set; }

    public DateTimeOffset? LeaseExpiresAtUtc { get; private set; }

    public bool CancellationRequested { get; private set; }

    public string? LastError { get; private set; }

    public Guid? MasterVideoProductionPlanId { get; private set; }

    public Guid? VideoNarrationId { get; private set; }

    public string? SocialCaptionLong { get; private set; }

    public string? SocialCaptionShort { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public Organization Organization { get; private set; } = null!;

    public ListingProperty Property { get; private set; } = null!;

    public VideoProductionPlan? MasterVideoProductionPlan { get; private set; }

    public VideoNarration? VideoNarration { get; private set; }

    public IReadOnlyCollection<CampaignDeliverable> Deliverables => deliverables;

    private readonly List<CampaignDeliverable> deliverables = [];

    public IReadOnlyCollection<CampaignStillPost> StillPosts => stillPosts;

    private readonly List<CampaignStillPost> stillPosts = [];

    public static CampaignGenerationJob Create(
        Guid organizationId,
        Guid propertyId,
        string requestedByUserId,
        string sourceFingerprint,
        CampaignOutputSelection requestedOutputs,
        DateTimeOffset now,
        CampaignCreativeOptions? creativeOptions = null,
        CampaignNarrationPreferences? narrationPreferences = null)
    {
        if (organizationId == Guid.Empty || propertyId == Guid.Empty)
        {
            throw new ArgumentException("Organization and property identities are required.");
        }

        return new CampaignGenerationJob(
            organizationId,
            propertyId,
            requestedByUserId,
            sourceFingerprint,
            requestedOutputs,
            creativeOptions ?? CampaignCreativeOptions.Legacy,
            narrationPreferences ?? CampaignNarrationPreferences.Default,
            now);
    }

    /// <summary>Selects the experimental visual strategy before the job has started.</summary>
    public void RequestVideoStrategy(CampaignVideoStrategy strategy)
    {
        if (!Enum.IsDefined(strategy))
        {
            throw new ArgumentOutOfRangeException(nameof(strategy));
        }

        if (Status != CampaignGenerationStatus.Queued || CurrentStage != CampaignGenerationStage.ValidateProperty)
        {
            throw new InvalidOperationException("The campaign video strategy can only be chosen before work starts.");
        }

        VideoStrategy = strategy;
    }

    /// <summary>The kill-switch fallback. Existing campaign plans and renderer behavior remain the control.</summary>
    public void UseEstablishedVideoStrategy()
    {
        VideoStrategy = CampaignVideoStrategy.Established;
        FullTourProviderRequestId = null;
        FullTourGenerationVersion = null;
        FullTourSubmittedAtUtc = null;
        FullTourAssetPath = null;
        FullTourContentType = null;
        FullTourFileSize = null;
        FullTourDurationMs = null;
    }

    public void MarkFullTourSubmitted(string requestId, string generationVersion, DateTimeOffset now)
    {
        if (VideoStrategy != CampaignVideoStrategy.Kling4FullTour)
        {
            throw new InvalidOperationException("This campaign does not use the Kling full-tour strategy.");
        }

        FullTourProviderRequestId = Required(requestId, 200, nameof(requestId));
        FullTourGenerationVersion = Required(generationVersion, 200, nameof(generationVersion));
        FullTourSubmittedAtUtc = now;
        FullTourAssetPath = null;
        FullTourContentType = null;
        FullTourFileSize = null;
        FullTourDurationMs = null;
        UpdatedAtUtc = now;
    }

    public void MarkFullTourCompleted(
        string assetPath,
        string contentType,
        long fileSize,
        int durationMs,
        DateTimeOffset now)
    {
        if (VideoStrategy != CampaignVideoStrategy.Kling4FullTour
            || string.IsNullOrWhiteSpace(FullTourProviderRequestId)
            || fileSize <= 0
            || durationMs <= 0)
        {
            throw new InvalidOperationException("A submitted Kling full tour is required before completion.");
        }

        FullTourAssetPath = Required(assetPath, 1_024, nameof(assetPath));
        FullTourContentType = Required(contentType, 100, nameof(contentType));
        FullTourFileSize = fileSize;
        FullTourDurationMs = durationMs;
        UpdatedAtUtc = now;
    }

    public void ResetFullTourSubmission(DateTimeOffset now)
    {
        if (VideoStrategy != CampaignVideoStrategy.Kling4FullTour)
        {
            throw new InvalidOperationException("This campaign does not use the Kling full-tour strategy.");
        }

        FullTourProviderRequestId = null;
        FullTourGenerationVersion = null;
        FullTourSubmittedAtUtc = null;
        FullTourAssetPath = null;
        FullTourContentType = null;
        FullTourFileSize = null;
        FullTourDurationMs = null;
        UpdatedAtUtc = now;
    }

    public void BeginStage(DateTimeOffset now, TimeSpan leaseDuration)
    {
        if (Status is not (CampaignGenerationStatus.Queued or CampaignGenerationStatus.Running)
            || CancellationRequested)
        {
            throw new InvalidOperationException("The campaign job cannot begin a stage.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);

        Status = CampaignGenerationStatus.Running;
        StageAttemptCount++;
        TotalStageAttempts++;
        LeaseExpiresAtUtc = now.Add(leaseDuration);
        NextAttemptAtUtc = null;
        UpdatedAtUtc = now;
    }

    public void CompleteStage(CampaignGenerationStage nextStage, DateTimeOffset now)
    {
        if (Status != CampaignGenerationStatus.Running
            || CampaignGenerationStageOrder.Rank(nextStage) <= CampaignGenerationStageOrder.Rank(CurrentStage))
        {
            throw new InvalidOperationException("The campaign stage cannot be completed.");
        }

        CurrentStage = nextStage;
        Status = CampaignGenerationStatus.Queued;
        StageAttemptCount = 0;
        NextAttemptAtUtc = now;
        LeaseExpiresAtUtc = null;
        LastError = null;
        UpdatedAtUtc = now;
    }

    public void ContinueStage(DateTimeOffset now, TimeSpan delay)
    {
        if (Status != CampaignGenerationStatus.Running || delay < TimeSpan.Zero)
        {
            throw new InvalidOperationException("The campaign stage cannot be continued.");
        }

        Status = CampaignGenerationStatus.Queued;
        StageAttemptCount = 0;
        NextAttemptAtUtc = now.Add(delay);
        LeaseExpiresAtUtc = null;
        UpdatedAtUtc = now;
    }

    public void ScheduleRetry(string error, DateTimeOffset now, TimeSpan delay)
    {
        if (Status != CampaignGenerationStatus.Running || delay <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("The campaign stage cannot schedule a retry.");
        }

        Status = CampaignGenerationStatus.Queued;
        NextAttemptAtUtc = now.Add(delay);
        LeaseExpiresAtUtc = null;
        LastError = Required(error, 1_000, nameof(error));
        UpdatedAtUtc = now;
    }

    public void Fail(string error, DateTimeOffset now)
    {
        Status = CampaignGenerationStatus.Failed;
        NextAttemptAtUtc = null;
        LeaseExpiresAtUtc = null;
        LastError = Required(error, 1_000, nameof(error));
        UpdatedAtUtc = now;
    }

    public void Retry(DateTimeOffset now)
    {
        if (Status != CampaignGenerationStatus.Failed)
        {
            throw new InvalidOperationException("Only a failed campaign can be retried.");
        }

        Status = CampaignGenerationStatus.Queued;
        StageAttemptCount = 0;
        NextAttemptAtUtc = now;
        LeaseExpiresAtUtc = null;
        LastError = null;
        UpdatedAtUtc = now;
    }

    public void RequestCancellation(DateTimeOffset now)
    {
        if (Status is CampaignGenerationStatus.Completed or CampaignGenerationStatus.Cancelled)
        {
            return;
        }

        CancellationRequested = true;
        UpdatedAtUtc = now;
        if (Status is CampaignGenerationStatus.Queued or CampaignGenerationStatus.Failed)
        {
            Cancel(now);
        }
    }

    public void Cancel(DateTimeOffset now)
    {
        Status = CampaignGenerationStatus.Cancelled;
        NextAttemptAtUtc = null;
        LeaseExpiresAtUtc = null;
        LastError = null;
        UpdatedAtUtc = now;
        CompletedAtUtc = now;
    }

    public void SetMasterPlan(Guid planId)
    {
        if (planId == Guid.Empty)
        {
            throw new ArgumentException("A master plan identity is required.", nameof(planId));
        }

        MasterVideoProductionPlanId = planId;
    }

    public void SetNarration(Guid narrationId)
    {
        if (narrationId == Guid.Empty)
        {
            throw new ArgumentException("A narration identity is required.", nameof(narrationId));
        }

        VideoNarrationId = narrationId;
    }

    public void SetLocalizedCopy(string localizedCopyJson)
    {
        LocalizedCopyJson = Required(localizedCopyJson, 100_000, nameof(localizedCopyJson));
    }

    public void SetLongFormLocalizedCopy(string localizedCopyJson)
    {
        LongFormLocalizedCopyJson = Required(localizedCopyJson, 500_000, nameof(localizedCopyJson));
    }

    /// <summary>Asks for the vertical video library. Only before work starts, and only with a vertical output.</summary>
    public void RequestVerticalLibrary(
        VerticalVideoSelection selection,
        VerticalCampaignGoal? goal = null,
        IReadOnlyList<VerticalCreativeFormat>? formats = null,
        string? hookVariant = null)
    {
        if (hookVariant is not (null or "a" or "b")
            || (hookVariant == "b" && (selection != VerticalVideoSelection.AgentChosen || formats is not { Count: 1 })))
        {
            throw new ArgumentException("Hook B is only for one chosen format.", nameof(hookVariant));
        }

        if (selection is not (VerticalVideoSelection.RecommendedThree or VerticalVideoSelection.AllEligible
            or VerticalVideoSelection.AgentChosen))
        {
            throw new ArgumentOutOfRangeException(nameof(selection));
        }

        if (goal is { } chosenGoal && !Enum.IsDefined(chosenGoal))
        {
            throw new ArgumentOutOfRangeException(nameof(goal));
        }

        var value = selection == VerticalVideoSelection.AgentChosen
            ? FormatVerticalFormats(formats)
            : formats is { Count: > 0 }
                ? throw new ArgumentException("Formats are only kept for an agent-chosen selection.", nameof(formats))
                : null;

        if (Status != CampaignGenerationStatus.Queued || CurrentStage != CampaignGenerationStage.ValidateProperty)
        {
            throw new InvalidOperationException("The vertical video library can only be chosen before the campaign starts.");
        }

        if (!RequestedOutputs.HasFlag(CampaignOutputSelection.TikTok)
            && !RequestedOutputs.HasFlag(CampaignOutputSelection.InstagramReel)
            && !RequestedOutputs.HasFlag(CampaignOutputSelection.YouTubeShort))
        {
            throw new InvalidOperationException("The vertical video library needs a vertical output.");
        }

        VerticalSelection = selection;
        VerticalGoal = goal;
        VerticalFormatsValue = value;
        VerticalHookVariant = hookVariant == "b" ? hookVariant : null;
    }

    /// <summary>The stored form of an agent's format list: distinct, in order, library formats only.</summary>
    public static string FormatVerticalFormats(IReadOnlyList<VerticalCreativeFormat>? formats)
    {
        if (formats is null || formats.Count == 0)
        {
            throw new ArgumentException("Choose at least one vertical video.", nameof(formats));
        }

        if (formats.Count > MaximumVerticalFormats)
        {
            throw new ArgumentException($"Choose at most {MaximumVerticalFormats} vertical videos.", nameof(formats));
        }

        if (formats.Any(format => format == VerticalCreativeFormat.LegacySingle || !Enum.IsDefined(format)))
        {
            throw new ArgumentOutOfRangeException(nameof(formats));
        }

        if (formats.Distinct().Count() != formats.Count)
        {
            throw new ArgumentException("Each vertical video format can be chosen once.", nameof(formats));
        }

        return string.Join(',', formats);
    }

    private static VerticalCreativeFormat[] ParseVerticalFormats(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                // An unknown name (written by a newer release) is skipped, never guessed.
                .Select(name => Enum.TryParse<VerticalCreativeFormat>(name, out var format) ? format : (VerticalCreativeFormat?)null)
                .OfType<VerticalCreativeFormat>()
                .Where(format => format != VerticalCreativeFormat.LegacySingle)
                .Distinct()
                .ToArray();

    /// <summary>Falls back to the one shared vertical edit (feature switched off, or no format can be made).</summary>
    public void UseLegacyVertical()
    {
        VerticalSelection = null;
        VerticalFormatsValue = null;
        VerticalHookVariant = null;
    }

    public void SetShortFormLocalizedCopy(string localizedCopyJson)
    {
        ShortFormLocalizedCopyJson = Required(localizedCopyJson, 500_000, nameof(localizedCopyJson));
    }

    public CampaignCreativeOptions CreativeOptions => new(
        CreativeElements,
        Soundtrack,
        CaptionStyle,
        AgentPortraitId,
        MarketingTemplate,
        PriceReductionAmount,
        PriceReductionVerified,
        AgentAvatarConsentConfirmed,
        Pace,
        ShortFormVoiceover,
        OpenHouseText,
        OpenHouseVerified);

    public void SetSocialCopy(string longCaption, string shortCaption)
    {
        SocialCaptionLong = Required(longCaption, 4_000, nameof(longCaption));
        SocialCaptionShort = Required(shortCaption, 1_000, nameof(shortCaption));
    }

    public void Complete(DateTimeOffset now)
    {
        if (Status != CampaignGenerationStatus.Running
            || CurrentStage != CampaignGenerationStage.FinalizeCampaign)
        {
            throw new InvalidOperationException("The campaign cannot be finalized from its current stage.");
        }

        Status = CampaignGenerationStatus.Completed;
        StageAttemptCount = 0;
        NextAttemptAtUtc = null;
        LeaseExpiresAtUtc = null;
        LastError = null;
        UpdatedAtUtc = now;
        CompletedAtUtc = now;
    }

    private static string Required(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return normalized;
    }
}
