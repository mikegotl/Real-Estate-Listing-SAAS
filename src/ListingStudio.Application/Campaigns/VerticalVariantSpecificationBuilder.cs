using System.Text.Json;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Videos;

namespace ListingStudio.Application.Campaigns;

/// <summary>Pixel size of a storyboard photo, for framing it in 9:16.</summary>
public sealed record VerticalVariantPhotoSize(Guid MediaId, int Width, int Height);

/// <summary>Size and length of a finished property video.</summary>
public sealed record VerticalVariantVideoSize(Guid VideoId, int Width, int Height, int DurationMs);

/// <summary>
/// Turns one format's fixed recipe into a renderable 9:16 video plan. Every scene, line and voice segment comes from
/// the recipe; the campaign's master plan only lends the brand, story identity and music. Nothing here invents copy.
/// </summary>
public static class VerticalVariantSpecificationBuilder
{
    public const string BuilderVersion = "vertical-variant-builder-v1";

    private static readonly VideoOutputProfile Output = new(1_080, 1_920, 30, "h264", "aac", "yuv420p", 48_000, 2);
    private static readonly NormalizedRect SafeZone = new(0.075m, 0.05m, 0.85m, 0.9m);

    public static VideoProductionSpecification Build(
        VideoProductionSpecification master,
        VerticalCreativeRecipe recipe,
        IReadOnlyCollection<VerticalVariantPhotoSize> photos,
        IReadOnlyCollection<VerticalVariantVideoSize> videos)
    {
        ArgumentNullException.ThrowIfNull(master);
        ArgumentNullException.ThrowIfNull(recipe);
        var problems = recipe.Validate();
        if (problems.Count > 0)
        {
            throw new InvalidOperationException($"The {recipe.Format} recipe is invalid: {string.Join(" ", problems)}");
        }

        var photoSizes = photos.ToDictionary(photo => photo.MediaId);
        var videoSizes = videos.ToDictionary(video => video.VideoId);
        var programMs = recipe.DurationSeconds * 1_000;
        var beats = recipe.Beats.OrderBy(beat => beat.Order).ToArray();
        var fallbackPhoto = FallbackPhoto(recipe, beats, photoSizes);
        var scenes = new List<VideoScene>(beats.Length);
        var narration = new List<NarrationSegment>();
        for (var index = 0; index < beats.Length; index++)
        {
            var beat = beats[index];
            var (visual, motion) = Visual(beat, index, fallbackPhoto, photoSizes, videoSizes);
            var overlays = new List<TextOverlay>();
            if (beat.Overlay is { } line)
            {
                overlays.Add(Overlay(beat, line, recipe.CallToAction));
            }

            var segmentIds = new List<string>();
            if (beat.Narration is { } spoken && recipe.Narration != VerticalNarrationStructure.MusicOnly)
            {
                var id = $"variant-{beat.Order}";
                narration.Add(new NarrationSegment(id, beat.StartMs, beat.DurationMs, spoken.Text, spoken.GroundingKey));
                segmentIds.Add(id);
            }

            scenes.Add(new VideoScene(
                index + 1,
                beat.StartMs,
                beat.DurationMs,
                visual,
                index == 0 || recipe.Format is VerticalCreativeFormat.WowFeature or VerticalCreativeFormat.SpeedTour
                    ? new TransitionPlan(TransitionKind.Cut, 0)
                    : new TransitionPlan(TransitionKind.Crossfade, Math.Min(300, Math.Min(beat.DurationMs, beats[index - 1].DurationMs) / 3)),
                motion,
                overlays,
                [],
                segmentIds));
        }

        var recipeKeys = recipe.Facts.Select(fact => fact.Key).ToHashSet(StringComparer.Ordinal);
        var bindings = master.FactBindings
            .Where(binding => !recipeKeys.Contains(binding.Key))
            .Concat(recipe.Facts.Select(fact => new FactBinding(
                fact.Key,
                fact.Value,
                ToFactSource(fact.Source),
                fact.Source == VerticalCopySource.ApprovedTemplate
                    ? $"Approved format template {recipe.RecipeVersion}"
                    : fact.Reference)))
            .ToArray();
        var music = master.Audio.Music.AssetId is null
            ? master.Audio.Music
            : master.Audio.Music with
            {
                StartMs = 0,
                DurationMs = programMs,
                FadeInMs = Math.Min(master.Audio.Music.FadeInMs, programMs / 4),
                FadeOutMs = Math.Min(master.Audio.Music.FadeOutMs, programMs / 4),
            };
        return master with
        {
            RequestedDuration = (RequestedDuration)recipe.DurationSeconds,
            AspectRatio = VideoAspectRatio.Vertical9By16,
            Output = Output,
            SafeZone = SafeZone,
            FactBindings = bindings,
            CallToAction = new GroundedText(recipe.CallToAction.Text, recipe.CallToAction.GroundingKey),
            Audio = new AudioPlan(narration, music),
            Scenes = scenes,
        };
    }

    /// <summary>
    /// Maps a recipe line's origin onto the persisted fact sources, which stay append-only so older readers of stored
    /// plans keep working. Agent claims, campaign inputs and storyboard labels are all agent-confirmed inputs.
    /// </summary>
    public static FactSource ToFactSource(VerticalCopySource source) => source switch
    {
        VerticalCopySource.VerifiedProperty => FactSource.VerifiedProperty,
        VerticalCopySource.AgentNarration => FactSource.AcceptedScript,
        VerticalCopySource.AgentClaim => FactSource.AgentVerifiedCampaignInput,
        VerticalCopySource.AgentCampaignInput => FactSource.AgentVerifiedCampaignInput,
        VerticalCopySource.AgentStoryboard => FactSource.AgentVerifiedCampaignInput,
        VerticalCopySource.MediaAnalysis => FactSource.PropertyStory,
        VerticalCopySource.ApprovedTemplate => FactSource.BrandKit,
        VerticalCopySource.PropertyStory => FactSource.PropertyStory,
        VerticalCopySource.ApprovedNeighborhood => FactSource.ApprovedNeighborhood,
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    /// <summary>
    /// The campaign-wide options adjusted for one format: the format decides the voice and whether word captions are
    /// burned in; the presenter only appears in a hosted video the agent already consented to.
    /// </summary>
    public static CampaignCreativeOptions OptionsFor(CampaignCreativeOptions campaign, VerticalCreativeRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(recipe);
        var elements = campaign.Elements;
        elements = recipe.CaptionDensity == VerticalCaptionDensity.WordTimed
            ? elements | CampaignVideoElement.NarrationCaptions
            : elements & ~CampaignVideoElement.NarrationCaptions;
        if (!recipe.UsesPresenter || !campaign.AgentAvatarConsentConfirmed || campaign.AgentPortraitId is null)
        {
            elements &= ~CampaignVideoElement.AgentAvatar;
        }

        // A format without the presenter must not keep the presenter's consent or a portrait nothing shows,
        // or the options fail validation and that video is never made.
        var keepsPortrait = elements.HasFlag(CampaignVideoElement.AgentPortrait)
            || elements.HasFlag(CampaignVideoElement.AgentAvatar);
        return campaign with
        {
            Elements = elements,
            AgentPortraitId = keepsPortrait ? campaign.AgentPortraitId : null,
            AgentAvatarConsentConfirmed = elements.HasFlag(CampaignVideoElement.AgentAvatar)
                && campaign.AgentAvatarConsentConfirmed,
            Voiceover = recipe.Narration == VerticalNarrationStructure.MusicOnly
                ? CampaignVoiceoverMode.None
                : CampaignVoiceoverMode.ShortHook,
        };
    }

    private static Guid? FallbackPhoto(
        VerticalCreativeRecipe recipe,
        IReadOnlyList<VerticalRecipeBeat> beats,
        Dictionary<Guid, VerticalVariantPhotoSize> photos)
    {
        if (recipe.PrimaryPropertyMediaId is { } primary && photos.ContainsKey(primary))
        {
            return primary;
        }

        return beats.Select(beat => beat.PropertyMediaId).FirstOrDefault(id => id is { } value && photos.ContainsKey(value));
    }

    private static (VisualSource Visual, MotionPlan Motion) Visual(
        VerticalRecipeBeat beat,
        int index,
        Guid? fallbackPhoto,
        Dictionary<Guid, VerticalVariantPhotoSize> photos,
        Dictionary<Guid, VerticalVariantVideoSize> videos)
    {
        if (beat.PropertyVideoId is { } videoId
            && videos.TryGetValue(videoId, out var video)
            && video.DurationMs >= beat.DurationMs)
        {
            var start = Math.Clamp(beat.SourceOffsetMs, 0, video.DurationMs - beat.DurationMs);
            var fill = Fill(video.Width, video.Height);
            return (
                new VisualSource(VisualSourceKind.PropertyVideo, null, null, null, null, videoId, start),
                new MotionPlan(MotionKind.None, fill, fill, MotionEasing.Linear));
        }

        // A presenter beat, or footage too short for its beat, shows the format's primary photo underneath.
        var mediaId = beat.PropertyMediaId is { } own && photos.ContainsKey(own) ? own : fallbackPhoto;
        if (mediaId is not { } photoId || !photos.TryGetValue(photoId, out var photo))
        {
            throw new InvalidOperationException($"Beat {beat.Order} has no photo or footage that can be shown.");
        }

        var frame = Fill(photo.Width, photo.Height);
        var motion = beat.Treatment == VerticalVisualTreatment.TextCardOverPhoto
            ? new MotionPlan(MotionKind.KenBurns, frame, Scale(frame, 0.94m), MotionEasing.Linear)
            : (index % 2) == 0
                ? new MotionPlan(MotionKind.KenBurns, frame, Scale(frame, 0.86m), MotionEasing.EaseInOut)
                : new MotionPlan(MotionKind.KenBurns, Scale(frame, 0.86m), frame, MotionEasing.EaseInOut);
        return (new VisualSource(VisualSourceKind.PropertyMedia, photoId, null, null, null), motion);
    }

    private static TextOverlay Overlay(VerticalRecipeBeat beat, VerticalRecipeLine line, VerticalRecipeLine callToAction)
    {
        var id = $"variant-{beat.Order}-{beat.Kind.ToString().ToLowerInvariant()}";
        if (beat.Kind == VerticalBeatKind.CallToAction || line.GroundingKey == callToAction.GroundingKey)
        {
            return new TextOverlay(id, line.Text, line.GroundingKey, 0, beat.DurationMs,
                OverlayAnchor.Center, new NormalizedRect(0.08m, 0.30m, 0.84m, 0.12m), TextOverlayStyle.ClosingCta);
        }

        if (beat.Treatment == VerticalVisualTreatment.TextCardOverPhoto)
        {
            return new TextOverlay(id, line.Text, line.GroundingKey, 0, beat.DurationMs,
                OverlayAnchor.Center, new NormalizedRect(0.08m, 0.40m, 0.84m, 0.14m), TextOverlayStyle.HookTitle);
        }

        return beat.Kind switch
        {
            VerticalBeatKind.Hook or VerticalBeatKind.PriceReveal or VerticalBeatKind.AccessHook
                or VerticalBeatKind.Problem or VerticalBeatKind.HostIntro => new TextOverlay(
                    id, line.Text, line.GroundingKey, 0, beat.DurationMs,
                    OverlayAnchor.TopCenter, new NormalizedRect(0.08m, 0.16m, 0.84m, 0.12m), TextOverlayStyle.HookTitle),
            VerticalBeatKind.EssentialFacts or VerticalBeatKind.PriceRecap or VerticalBeatKind.ValueMessage => new TextOverlay(
                id, line.Text, line.GroundingKey, 0, beat.DurationMs,
                OverlayAnchor.TopCenter, new NormalizedRect(0.08m, 0.30m, 0.84m, 0.08m), TextOverlayStyle.HookFact),
            // Room labels sit below TikTok's Following / For You tabs (the top ~12% of the screen) and are big enough
            // to read at a glance.
            _ => new TextOverlay(
                id, line.Text, line.GroundingKey, 0, beat.DurationMs,
                OverlayAnchor.TopCenter, new NormalizedRect(0.08m, 0.14m, 0.84m, 0.065m), TextOverlayStyle.SpecLine),
        };
    }

    /// <summary>The largest centred 9:16 window of a source.</summary>
    private static NormalizedRect Fill(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("A source needs positive dimensions.");
        }

        var normalizedWidth = (decimal)Output.Width / Output.Height * height / width;
        return normalizedWidth <= 1
            ? new NormalizedRect((1 - normalizedWidth) / 2, 0, normalizedWidth, 1)
            : new NormalizedRect(0, (1 - 1 / normalizedWidth) / 2, 1, 1 / normalizedWidth);
    }

    private static NormalizedRect Scale(NormalizedRect fill, decimal factor)
    {
        var width = fill.Width * factor;
        var height = fill.Height * factor;
        return new NormalizedRect(fill.X + (fill.Width - width) / 2, fill.Y + (fill.Height - height) / 2, width, height);
    }
}

/// <summary>One vertical video's own post copy, built only from its recipe's grounded lines.</summary>
public sealed record VerticalVariantCaptions(
    string Format,
    string ShortCaption,
    string LongCaption,
    string Hook,
    string CallToAction,
    IReadOnlyList<string> GroundingKeys)
{
    public const string CaptionVersion = "vertical-captions-v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static VerticalVariantCaptions? FromJson(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<VerticalVariantCaptions>(json, JsonOptions);

    /// <summary>The hook, the verified facts the video shows, and its call to action. No new claims.</summary>
    public static VerticalVariantCaptions From(VerticalCreativeRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        var facts = recipe.Facts.ToDictionary(fact => fact.Key, StringComparer.Ordinal);
        var used = new List<string> { recipe.Hook.GroundingKey };
        var lines = new List<string> { recipe.Hook.Text };
        foreach (var key in new[] { "property.essentialFacts", "property.cityState" })
        {
            if (facts.TryGetValue(key, out var fact) && !recipe.Hook.Text.Contains(fact.Value, StringComparison.Ordinal))
            {
                lines.Add(fact.Value);
                used.Add(key);
            }
        }

        lines.Add(recipe.CallToAction.Text);
        used.Add(recipe.CallToAction.GroundingKey);
        return new VerticalVariantCaptions(
            recipe.Format.ToString(),
            $"{recipe.Hook.Text} {recipe.CallToAction.Text}",
            string.Join("\n", lines),
            recipe.Hook.Text,
            recipe.CallToAction.Text,
            used.Distinct(StringComparer.Ordinal).ToArray());
    }
}
