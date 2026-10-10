using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ListingStudio.Application.Properties;
using ListingStudio.Application.Stories;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Properties;
using ListingStudio.Domain.Stories;
using ListingStudio.Domain.Videos;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ListingStudio.Infrastructure.Videos;

public sealed partial class VideoProductionPlanService(
    ApplicationDbContext dbContext,
    IVideoDirector director,
    IVideoProductionSpecificationValidator validator,
    IPropertyStoryGroundingValidator storyValidator,
    ILogger<VideoProductionPlanService> logger) : IVideoProductionPlanService
{
    public async Task<VideoProductionPlanResult?> GetLatestAsync(
        string userId,
        Guid propertyId,
        RequestedDuration duration,
        VideoAspectRatio aspectRatio,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var plan = await dbContext.VideoProductionPlans
            .AsNoTracking()
            .Where(candidate => candidate.OrganizationId == organizationId
                && candidate.PropertyId == propertyId
                && candidate.RequestedDuration == duration
                && candidate.AspectRatio == aspectRatio)
            .OrderByDescending(candidate => candidate.Version)
            .FirstOrDefaultAsync(cancellationToken);
        return plan is null ? null : ToResult(plan, reused: false);
    }

    public async Task<VideoProductionPlanResult?> GenerateAsync(
        string userId,
        Guid propertyId,
        RequestedDuration duration,
        VideoAspectRatio aspectRatio,
        CancellationToken cancellationToken = default)
    {
        EnsureSupportedOutput(duration, aspectRatio);
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        // Lock before source assembly/provider calls to prevent duplicate spend and racing property edits.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var property = await dbContext.Properties
            .FromSqlInterpolated($"SELECT * FROM \"Properties\" WHERE \"Id\" = {propertyId} AND \"OrganizationId\" = {organizationId} FOR UPDATE")
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == propertyId && candidate.OrganizationId == organizationId,
                cancellationToken);
        if (property is null)
        {
            return null;
        }

        if (property.IsArchived)
        {
            throw new InvalidOperationException("Archived properties cannot generate video production plans.");
        }

        var media = await dbContext.PropertyMedia
            .FromSqlInterpolated($"SELECT * FROM \"PropertyMedia\" WHERE \"PropertyId\" = {propertyId} AND \"OrganizationId\" = {organizationId} ORDER BY \"DisplayOrder\", \"Id\" FOR UPDATE")
            .AsNoTracking().ToArrayAsync(cancellationToken);
        if (media.Length == 0
            || media.Any(media => media.AnalysisStatus != PropertyMediaAnalysisStatus.Completed))
        {
            throw new InvalidOperationException(
                "Every property image must have completed analysis before video direction.");
        }

        var story = await dbContext.PropertyStories
            .AsNoTracking()
            .Where(candidate => candidate.OrganizationId == organizationId && candidate.PropertyId == propertyId)
            .OrderByDescending(candidate => candidate.Version)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("A grounded property story is required before video direction.");

        var organizationName = await dbContext.Organizations
            .AsNoTracking()
            .Where(organization => organization.Id == organizationId)
            .Select(organization => organization.Name)
            .SingleAsync(cancellationToken);
        var approvedNeighborhoodFacts = await dbContext.NeighborhoodInsights
            .AsNoTracking()
            .Where(insight => insight.OrganizationId == organizationId
                && insight.PropertyId == propertyId
                && insight.IsApproved)
            .OrderBy(insight => insight.Category)
            .ThenBy(insight => insight.DistanceMiles)
            .Select(insight => new ApprovedNeighborhoodFact(
                insight.Category.ToString(),
                insight.Name,
                insight.Address,
                insight.DistanceMiles,
                insight.SourceUrl,
                insight.CheckedAtUtc,
                insight.Id))
            .ToArrayAsync(cancellationToken);
        var narrationScript = await dbContext.PropertyNarrationScripts
            .AsNoTracking()
            .SingleOrDefaultAsync(script => script.OrganizationId == organizationId
                && script.PropertyId == propertyId
                && script.MarketingUseAccepted,
                cancellationToken);
        if (narrationScript is not null
            && NarrationScriptPolicy.RequiresLongForm(narrationScript.ExtractedText)
            && duration == RequestedDuration.Hero60)
        {
            narrationScript = null;
        }
        var walkthroughVideos = await dbContext.PropertyVideos
            .AsNoTracking()
            .Where(video => video.OrganizationId == organizationId
                && video.PropertyId == propertyId
                && video.ProcessingStatus == PropertyVideoProcessingStatus.Completed
                && video.EnhancedBlobPath != null)
            .OrderBy(video => video.UploadedAtUtc)
            .ToArrayAsync(cancellationToken);
        var request = CreateRequest(
            property,
            media,
            story,
            organizationName,
            approvedNeighborhoodFacts,
            narrationScript,
            walkthroughVideos,
            duration,
            aspectRatio);
        var storySource = new PropertyStoryGenerationRequest(request.VerifiedProperty,
            request.Media.Select(m => m.Analysis).ToArray(), new(organizationName, request.Brand.AgentName),
            approvedNeighborhoodFacts);
        if (!storyValidator.Validate(storySource, story.GetContent()).IsValid)
            throw new InvalidDataException("Stored property story no longer passes grounding against current verified facts. Generate a new story.");
        var fingerprint = CreateFingerprint(director.DirectorVersion, request);
        var existing = await FindByFingerprintAsync(organizationId, propertyId, fingerprint, cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return ToResult(existing, reused: true);
        }

        var generated = await GenerateValidatedSpecificationAsync(request, cancellationToken);
        var specification = generated.Specification;

        var specificationJson = JsonSerializer.Serialize(specification, VideoSpecificationJson.Options);
        var latestVersion = await dbContext.VideoProductionPlans
            .Where(candidate => candidate.OrganizationId == organizationId
                && candidate.PropertyId == propertyId
                && candidate.RequestedDuration == duration
                && candidate.AspectRatio == aspectRatio)
            .Select(candidate => (int?)candidate.Version)
            .MaxAsync(cancellationToken) ?? 0;
        var plan = VideoProductionPlan.Create(
            organizationId,
            propertyId,
            story.Id,
            latestVersion + 1,
            duration,
            aspectRatio,
            specification.SchemaVersion,
            generated.DirectorVersion,
            fingerprint,
            specificationJson);
        dbContext.VideoProductionPlans.Add(plan);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToResult(plan, reused: false);
    }

    private async Task<ValidatedSpecification> GenerateValidatedSpecificationAsync(
        VideoDirectionRequest request,
        CancellationToken cancellationToken)
    {
        DirectedEditorialPlan initialPlan;
        try
        {
            initialPlan = await director.DirectAsync(request, cancellationToken: cancellationToken);
        }
        catch (InvalidDataException exception)
        {
            LogInvalidProviderResponse(exception, request.PropertyId);
            return CreateFallbackOrThrow(request, ["The provider response was incomplete or malformed."]);
        }

        var initialSpecification = CreateSpecification(request, initialPlan);
        var initialValidation = validator.Validate(request, initialSpecification);
        if (initialValidation.IsValid)
        {
            return new(initialSpecification, director.DirectorVersion);
        }

        LogValidationFailure(request.PropertyId, "initial", initialValidation.Errors);
        DirectedEditorialPlan repairedPlan;
        try
        {
            repairedPlan = await director.DirectAsync(
                request,
                initialValidation.Errors,
                cancellationToken);
        }
        catch (InvalidDataException exception)
        {
            LogInvalidProviderResponse(exception, request.PropertyId);
            return CreateFallbackOrThrow(request, initialValidation.Errors);
        }

        var repairedSpecification = CreateSpecification(request, repairedPlan);
        var repairedValidation = validator.Validate(request, repairedSpecification);
        if (repairedValidation.IsValid)
        {
            return new(repairedSpecification, director.DirectorVersion);
        }

        LogValidationFailure(request.PropertyId, "repair", repairedValidation.Errors);
        return CreateFallbackOrThrow(request, repairedValidation.Errors);
    }

    private ValidatedSpecification CreateFallbackOrThrow(
        VideoDirectionRequest request,
        IReadOnlyList<string> validationErrors)
    {
        if (request.Media.Count != 1)
        {
            throw new VideoPlanValidationException(
                "Listing Studio could not create a valid master video plan after a guided repair. "
                + "Confirm that every photo finished analysis, add clear interior and exterior photos if coverage is limited, "
                + "then retry the failed stage.",
                validationErrors);
        }

        LogFallbackUsed(request.PropertyId);
        var fallback = CreateSpecification(request, CreateSinglePhotoFallback(request));
        var fallbackValidation = validator.Validate(request, fallback);
        if (!fallbackValidation.IsValid)
        {
            throw new VideoPlanValidationException(
                "Listing Studio could not create a valid master video plan from the available photo. "
                + "Upload additional analyzed interior and exterior photos, then retry the failed stage.",
                fallbackValidation.Errors);
        }

        return new(fallback, "deterministic-single-photo-v1");
    }

    private static VideoProductionSpecification CreateSpecification(
        VideoDirectionRequest request,
        DirectedEditorialPlan editorialPlan)
    {
        var media = request.Media.ToDictionary(item => item.MediaId);
        var walkthroughs = (request.WalkthroughVideos ?? []).ToDictionary(item => item.PropertyVideoId);
        var scenes = editorialPlan.Scenes.Select(scene =>
        {
            if (scene.VisualSource.Kind == VisualSourceKind.PropertyVideo
                && scene.VisualSource.PropertyVideoId is { } videoId
                && walkthroughs.TryGetValue(videoId, out var walkthrough))
            {
                var videoViewport = CreateAspectFillViewport(
                    walkthrough.Width,
                    walkthrough.Height,
                    request.AspectRatio);
                return scene with
                {
                    Motion = new MotionPlan(MotionKind.None, videoViewport, videoViewport, MotionEasing.Linear),
                };
            }

            var mediaId = scene.VisualSource.PropertyMediaId
                ?? scene.VisualSource.FallbackPropertyMediaId;
            if (mediaId is not { } id || !media.TryGetValue(id, out var source))
            {
                return scene;
            }

            var viewport = CreateAspectFillViewport(source, request.AspectRatio);
            return scene with
            {
                Motion = new MotionPlan(
                    MotionKind.None,
                    viewport,
                    viewport,
                    MotionEasing.Linear),
            };
        }).ToArray();
        var audio = request.ApprovedMusicAssetIds.Count == 0
            ? editorialPlan.Audio with
            {
                Music = new MusicPlan(null, MusicMood.None, 0, 0, 0, 0, 0, 0),
            }
            : editorialPlan.Audio;

        return new VideoProductionSpecification(
            "1.0",
            request.PropertyId,
            request.PropertyStory.Id,
            request.PropertyStory.Version,
            request.RequestedDuration,
            request.AspectRatio,
            request.Output,
            request.SafeZone,
            request.FactBindings,
            request.Brand,
            request.CallToAction,
            audio,
            scenes);
    }

    private static DirectedEditorialPlan CreateSinglePhotoFallback(VideoDirectionRequest request)
    {
        var media = request.Media.Single();
        var durationMs = (int)request.RequestedDuration * 1_000;
        var firstDurationMs = durationMs * 2 / 3;
        var secondDurationMs = durationMs - firstDurationMs;
        var narrationBindings = request.AcceptedNarrationScript is { } accepted
            ? accepted.Segments.Select(segment => new GroundedText(segment.Text, segment.Key)).ToArray()
            : [new GroundedText(
                request.FactBindings.Single(binding => binding.Key == "story.voiceover").Value,
                "story.voiceover")];
        var narrationWindowMs = durationMs - 6_000;
        var totalWords = narrationBindings.Sum(binding => CountWords(binding.Text));
        var narration = new List<NarrationSegment>(narrationBindings.Length);
        var narrationStart = 500;
        var allocated = 0;
        for (var index = 0; index < narrationBindings.Length; index++)
        {
            var remaining = narrationWindowMs - allocated;
            var segmentDuration = index == narrationBindings.Length - 1
                ? remaining
                : Math.Max(1_000, narrationWindowMs * CountWords(narrationBindings[index].Text) / totalWords);
            segmentDuration = Math.Min(segmentDuration, remaining - (narrationBindings.Length - index - 1));
            narration.Add(new NarrationSegment(
                $"narration-{index + 1}",
                narrationStart + allocated,
                segmentDuration,
                narrationBindings[index].Text,
                narrationBindings[index].GroundingKey));
            allocated += segmentDuration;
        }

        var viewport = CreateAspectFillViewport(media, request.AspectRatio);
        var visualSource = new VisualSource(
            VisualSourceKind.PropertyMedia,
            media.MediaId,
            null,
            null,
            null);
        var motion = new MotionPlan(MotionKind.None, viewport, viewport, MotionEasing.Linear);
        return new DirectedEditorialPlan(
            new AudioPlan(
                narration,
                new MusicPlan(null, MusicMood.None, 0, 0, 0, 0, 0, 0)),
            [
                new VideoScene(
                    1,
                    0,
                    firstDurationMs,
                    visualSource,
                    new TransitionPlan(TransitionKind.Cut, 0),
                    motion,
                    [],
                    [],
                    narration.Where(segment => segment.StartMs < firstDurationMs
                        && segment.StartMs + segment.DurationMs > 0)
                        .Select(segment => segment.Id).ToArray()),
                new VideoScene(
                    2,
                    firstDurationMs,
                    secondDurationMs,
                    visualSource,
                    new TransitionPlan(TransitionKind.Crossfade, 500),
                    motion,
                    [new TextOverlay(
                        "closing-cta",
                        request.CallToAction.Text,
                        request.CallToAction.GroundingKey,
                        secondDurationMs - 5_000,
                        4_000,
                        OverlayAnchor.BottomCenter,
                        new NormalizedRect(0.15m, 0.75m, 0.7m, 0.1m),
                        TextOverlayStyle.ClosingCta)],
                    [],
                    narration.Where(segment => segment.StartMs < durationMs
                        && segment.StartMs + segment.DurationMs > firstDurationMs)
                        .Select(segment => segment.Id).ToArray()),
            ]);
    }

    private static int CountWords(string value) => value.Split(
        (char[]?)null,
        StringSplitOptions.RemoveEmptyEntries).Length;

    private static NormalizedRect CreateAspectFillViewport(
        VideoMediaInput media,
        VideoAspectRatio aspectRatio) => CreateAspectFillViewport(media.Width, media.Height, aspectRatio);

    private static NormalizedRect CreateAspectFillViewport(
        int width,
        int height,
        VideoAspectRatio aspectRatio)
    {
        var outputAspect = aspectRatio == VideoAspectRatio.Landscape16By9 ? 16m / 9m : 9m / 16m;
        var normalizedAspect = outputAspect * height / width;
        return normalizedAspect >= 1
            ? new NormalizedRect(0, (1 - 1 / normalizedAspect) / 2, 1, 1 / normalizedAspect)
            : new NormalizedRect((1 - normalizedAspect) / 2, 0, normalizedAspect, 1);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Video plan for property {PropertyId} failed {Attempt} semantic validation: {ValidationErrors}")]
    private partial void LogValidationFailure(
        Guid propertyId,
        string attempt,
        string validationErrors);

    private void LogValidationFailure(
        Guid propertyId,
        string attempt,
        IReadOnlyList<string> validationErrors) =>
        LogValidationFailure(propertyId, attempt, string.Join(" | ", validationErrors));

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Video director returned invalid structured data for property {PropertyId}")]
    private partial void LogInvalidProviderResponse(Exception exception, Guid propertyId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Using the deterministic single-photo video plan fallback for property {PropertyId}")]
    private partial void LogFallbackUsed(Guid propertyId);

    private sealed record ValidatedSpecification(
        VideoProductionSpecification Specification,
        string DirectorVersion);

    private static VideoDirectionRequest CreateRequest(
        ListingProperty property,
        IReadOnlyList<PropertyMedia> propertyMedia,
        PropertyStory story,
        string organizationName,
        IReadOnlyList<ApprovedNeighborhoodFact> approvedNeighborhoodFacts,
        PropertyNarrationScript? narrationScript,
        IReadOnlyList<PropertyVideo> walkthroughVideos,
        RequestedDuration duration,
        VideoAspectRatio aspectRatio)
    {
        var verified = new VerifiedPropertyData(
            property.Address1,
            property.Address2,
            property.City,
            property.State,
            property.ZipCode,
            property.ListingPrice,
            property.Bedrooms,
            property.Bathrooms,
            property.SquareFeet,
            property.LotSize,
            property.YearBuilt,
            property.PropertyType,
            property.Description,
            property.ListingStatus);
        var content = story.GetContent();
        var storyInput = new VideoPropertyStoryInput(
            story.Id,
            story.Version,
            new PropertyStoryContentSnapshot(
                content.CampaignTitle,
                content.OpeningHook,
                content.PropertyNarrative,
                content.Highlights,
                content.VoiceoverScript,
                content.ClosingCta));
        var media = propertyMedia
            .OrderBy(item => item.DisplayOrder)
            .Select(item => new VideoMediaInput(
                item.Id,
                item.Width,
                item.Height,
                ToObservation(item, item.GetAnalysis()!)))
            .ToArray();
        var brand = new BrandKit(
            null,
            null,
            null,
            null,
            null,
            null,
            "#17324D",
            "#F4F0E8");
        var bindings = CreateFactBindings(
            verified,
            content,
            organizationName,
            approvedNeighborhoodFacts,
            narrationScript);
        var acceptedScript = narrationScript is null
            ? null
            : new AcceptedNarrationScriptInput(
                narrationScript.Id,
                narrationScript.MarketingUseAcceptedAtUtc!.Value,
                SplitAcceptedScript(narrationScript.ExtractedText)
                    .Select((text, index) => new AcceptedNarrationScriptSegment(
                        $"acceptedScript.segment.{index + 1}",
                        text))
                    .ToArray());
        var videos = walkthroughVideos.Select(video => new VideoWalkthroughInput(
            video.Id,
            video.OriginalFilename,
            video.EnhancedWidth!.Value,
            video.EnhancedHeight!.Value,
            video.EnhancedDurationMs!.Value)).ToArray();
        return new VideoDirectionRequest(
            property.Id,
            storyInput,
            verified,
            media,
            duration,
            aspectRatio,
            OutputFor(aspectRatio),
            SafeZoneFor(aspectRatio),
            bindings,
            brand,
            new GroundedText(content.ClosingCta, "story.closingCta"),
            new HashSet<Guid>(),
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            acceptedScript,
            videos);
    }

    private static List<FactBinding> CreateFactBindings(
        VerifiedPropertyData property,
        PropertyStoryContent story,
        string organizationName,
        IReadOnlyList<ApprovedNeighborhoodFact> approvedNeighborhoodFacts,
        PropertyNarrationScript? narrationScript)
    {
        var address = string.Join(", ", new[]
        {
            property.Address1,
            property.Address2,
            property.City,
            $"{property.State} {property.ZipCode}",
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var facts = new List<FactBinding>
        {
            new("property.address.full", address, FactSource.VerifiedProperty, "Address1+Address2+City+State+ZipCode"),
            new("property.listingPrice", property.ListingPrice.ToString(property.ListingPrice == decimal.Truncate(property.ListingPrice) ? "C0" : "C2", CultureInfo.GetCultureInfo("en-US")),
                FactSource.VerifiedProperty, "ListingPrice"),
            new("property.bedBath", $"{property.Bedrooms} beds • {property.Bathrooms:0.##} baths",
                FactSource.VerifiedProperty, "Bedrooms+Bathrooms"),
            new("story.campaignTitle", story.CampaignTitle, FactSource.PropertyStory, "CampaignTitle"),
            new("story.openingHook", story.OpeningHook, FactSource.PropertyStory, "OpeningHook"),
            new("story.propertyNarrative", story.PropertyNarrative, FactSource.PropertyStory, "PropertyNarrative"),
            new("story.voiceover", story.VoiceoverScript, FactSource.PropertyStory, "VoiceoverScript"),
            new("story.closingCta", story.ClosingCta, FactSource.PropertyStory, "ClosingCta"),
            new("brand.organizationName", organizationName, FactSource.BrandKit, "Organization.Name"),
        };
        if (property.SquareFeet is { } squareFeet)
        {
            facts.Add(new FactBinding(
                "property.bedBathSquareFeet",
                $"{property.Bedrooms} beds • {property.Bathrooms:0.##} baths • {squareFeet:N0} sq ft",
                FactSource.VerifiedProperty,
                "Bedrooms+Bathrooms+SquareFeet"));
        }

        for (var index = 0; index < story.Highlights.Count; index++)
        {
            facts.Add(new FactBinding(
                $"story.highlight.{index + 1}",
                story.Highlights[index],
                FactSource.PropertyStory,
                $"Highlights[{index}]"));
        }

        for (var index = 0; index < approvedNeighborhoodFacts.Count; index++)
        {
            var fact = approvedNeighborhoodFacts[index];
            facts.Add(new FactBinding(
                $"neighborhood.{index + 1}",
                $"{fact.Name} • {fact.Category} • {fact.DistanceMiles:0.##} miles straight-line distance",
                FactSource.ApprovedNeighborhood,
                fact.SourceUrl,
                fact.InsightId));
        }

        if (narrationScript is not null)
        {
            var segments = SplitAcceptedScript(narrationScript.ExtractedText);
            for (var index = 0; index < segments.Count; index++)
            {
                facts.Add(new FactBinding(
                    $"acceptedScript.segment.{index + 1}",
                    segments[index],
                    FactSource.AcceptedScript,
                    narrationScript.Id.ToString("D")));
            }
        }

        return facts;
    }

    private static List<string> SplitAcceptedScript(string text)
    {
        const int targetLength = 260;
        var sentences = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(paragraph => System.Text.RegularExpressions.Regex.Split(
                paragraph,
                @"(?<=[.!?])\s+"))
            .Where(sentence => !string.IsNullOrWhiteSpace(sentence))
            .Select(sentence => sentence.Trim())
            .ToArray();
        var result = new List<string>();
        var current = new StringBuilder();
        foreach (var sentence in sentences.SelectMany(value => SplitLongScriptSegment(value, targetLength)))
        {
            if (current.Length > 0 && current.Length + 1 + sentence.Length > targetLength)
            {
                result.Add(current.ToString());
                current.Clear();
            }

            if (current.Length > 0)
            {
                current.Append(' ');
            }

            current.Append(sentence);
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    private static IEnumerable<string> SplitLongScriptSegment(string value, int maximumLength)
    {
        var remaining = value.Trim();
        while (remaining.Length > maximumLength)
        {
            var split = remaining[..maximumLength].LastIndexOf(' ');
            if (split < maximumLength / 2)
            {
                split = maximumLength;
            }

            yield return remaining[..split].Trim();
            remaining = remaining[split..].TrimStart();
        }

        if (remaining.Length > 0)
        {
            yield return remaining;
        }
    }

    private static PropertyMediaObservation ToObservation(PropertyMedia media, PropertyMediaAnalysis analysis) => new(
        media.Id,
        media.DisplayOrder,
        analysis.Category,
        analysis.RoomType,
        analysis.QualityScore,
        analysis.HeroScore,
        analysis.IsExterior,
        analysis.IsInterior,
        analysis.ContainsPeople,
        analysis.PotentialProblems,
        analysis.Description,
        analysis.SuggestedDisplayOrder);

    private Task<VideoProductionPlan?> FindByFingerprintAsync(
        Guid organizationId,
        Guid propertyId,
        string fingerprint,
        CancellationToken cancellationToken) => dbContext.VideoProductionPlans
        .AsNoTracking()
        .SingleOrDefaultAsync(
            plan => plan.OrganizationId == organizationId
                && plan.PropertyId == propertyId
                && plan.SourceFingerprint == fingerprint,
            cancellationToken);

    private async Task<Guid> GetOrganizationIdAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var organizationId = await dbContext.OrganizationMembers
            .AsNoTracking()
            .Where(member => member.UserId == userId)
            .Select(member => (Guid?)member.OrganizationId)
            .FirstOrDefaultAsync(cancellationToken);
        return organizationId ?? throw new UnauthorizedAccessException("The user does not belong to an organization.");
    }

    private static string CreateFingerprint(string directorVersion, VideoDirectionRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directorVersion);
        var source = JsonSerializer.Serialize(new
        {
            schemaVersion = "1.0",
            directorVersion,
            request.PropertyId,
            request.PropertyStory,
            request.VerifiedProperty,
            request.Media,
            request.RequestedDuration,
            request.AspectRatio,
            request.Output,
            request.SafeZone,
            request.FactBindings,
            request.Brand,
            request.CallToAction,
            request.AcceptedNarrationScript,
            WalkthroughVideos = (request.WalkthroughVideos ?? []).OrderBy(video => video.PropertyVideoId).ToArray(),
            ApprovedGeneratedClipIds = request.ApprovedGeneratedClipIds.Order().ToArray(),
            ApprovedBrandAssetIds = request.ApprovedBrandAssetIds.Order(StringComparer.Ordinal).ToArray(),
            ApprovedMusicAssetIds = request.ApprovedMusicAssetIds.Order(StringComparer.Ordinal).ToArray(),
        }, VideoSpecificationJson.Options);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
    }

    private static VideoProductionPlanResult ToResult(VideoProductionPlan plan, bool reused)
    {
        var specification = JsonSerializer.Deserialize<VideoProductionSpecification>(
            plan.SpecificationJson,
            VideoSpecificationJson.Options)
            ?? throw new InvalidDataException("Stored video production specification is invalid.");
        return new VideoProductionPlanResult(
            plan.Id,
            plan.PropertyId,
            plan.PropertyStoryId,
            plan.Version,
            plan.DirectorVersion,
            specification,
            plan.CreatedAtUtc,
            reused);
    }

    private static VideoOutputProfile OutputFor(VideoAspectRatio aspectRatio) => aspectRatio switch
    {
        VideoAspectRatio.Landscape16By9 => new(1920, 1080, 30, "h264", "aac", "yuv420p", 48_000, 2),
        VideoAspectRatio.Vertical9By16 => new(1080, 1920, 30, "h264", "aac", "yuv420p", 48_000, 2),
        _ => throw new ArgumentOutOfRangeException(nameof(aspectRatio)),
    };

    private static NormalizedRect SafeZoneFor(VideoAspectRatio aspectRatio) => aspectRatio switch
    {
        VideoAspectRatio.Landscape16By9 => new(0.05m, 0.05m, 0.90m, 0.90m),
        VideoAspectRatio.Vertical9By16 => new(0.075m, 0.05m, 0.85m, 0.90m),
        _ => throw new ArgumentOutOfRangeException(nameof(aspectRatio)),
    };

    private static void EnsureSupportedOutput(RequestedDuration duration, VideoAspectRatio aspectRatio)
    {
        if ((int)duration is < 15 or > IPropertyNarrationScriptService.MaximumLongFormDurationSeconds
            || !Enum.IsDefined(aspectRatio))
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "The requested output is not supported.");
        }
    }
}
