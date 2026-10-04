using ListingStudio.Domain.Videos;
using System.Text.Json;

namespace ListingStudio.Application.Videos;

public sealed class VideoProductionSpecificationValidator : IVideoProductionSpecificationValidator
{
    public VideoSpecificationValidationResult Validate(
        VideoDirectionRequest authoritativeInput,
        VideoProductionSpecification specification)
    {
        ArgumentNullException.ThrowIfNull(authoritativeInput);
        ArgumentNullException.ThrowIfNull(specification);
        List<string> errors;
        try
        {
            errors = VideoSchemaContract.Validate(JsonSerializer.SerializeToElement(specification, VideoSpecificationJson.Options)).ToList();
        }
        catch (JsonException)
        {
            return new(false, ["Specification contains unsupported serialized values."]);
        }
        if (errors.Count > 0) return new(false, errors);

        ValidateAuthority(authoritativeInput, specification, errors);
        ValidateTimeline(authoritativeInput, specification, errors);
        ValidateAudioAndGrounding(authoritativeInput, specification, errors);

        return errors.Count == 0
            ? VideoSpecificationValidationResult.Success
            : new VideoSpecificationValidationResult(false, errors);
    }

    private static void ValidateAuthority(
        VideoDirectionRequest input,
        VideoProductionSpecification specification,
        List<string> errors)
    {
        AddIf(specification.SchemaVersion != "1.0", "schemaVersion must be 1.0.", errors);
        AddIf(specification.PropertyId != input.PropertyId, "propertyId is not authoritative.", errors);
        AddIf(specification.PropertyStoryId != input.PropertyStory.Id, "propertyStoryId is not authoritative.", errors);
        AddIf(specification.PropertyStoryVersion != input.PropertyStory.Version, "propertyStoryVersion is not authoritative.", errors);
        AddIf(specification.RequestedDuration != input.RequestedDuration, "requestedDuration is not authoritative.", errors);
        AddIf(specification.AspectRatio != input.AspectRatio, "aspectRatio is not authoritative.", errors);
        AddIf(specification.Output != input.Output, "output profile is not authoritative.", errors);
        AddIf(specification.SafeZone != input.SafeZone, "safeZone is not authoritative.", errors);
        AddIf(specification.Brand != input.Brand, "brand is not authoritative.", errors);
        AddIf(specification.CallToAction != input.CallToAction, "callToAction is not authoritative.", errors);
        AddIf(
            !specification.FactBindings.SequenceEqual(input.FactBindings),
            "factBindings must exactly match the authoritative allow-list.",
            errors);

        var expectedOutput = GetOutput(input.AspectRatio);
        AddIf(input.Output != expectedOutput, "The requested aspect ratio does not match the fixed output profile.", errors);
        AddIf(input.Media.Select(media => media.MediaId).Distinct().Count() != input.Media.Count,
            "The authoritative media set contains duplicate IDs.", errors);
    }

    private static void ValidateTimeline(
        VideoDirectionRequest input,
        VideoProductionSpecification specification,
        List<string> errors)
    {
        if (specification.Scenes.Count == 0)
        {
            errors.Add("scenes must not be empty.");
            return;
        }

        var media = input.Media
            .GroupBy(item => item.MediaId)
            .ToDictionary(group => group.Key, group => group.First());
        var overlayIds = new HashSet<string>(StringComparer.Ordinal);
        long expectedStart = 0;
        for (var index = 0; index < specification.Scenes.Count; index++)
        {
            var scene = specification.Scenes[index];
            var path = $"scenes[{index}]";
            AddIf(scene.SceneNumber != index + 1, $"{path}.sceneNumber must be consecutive.", errors);
            AddIf(scene.StartMs != expectedStart, $"{path}.startMs must be contiguous.", errors);
            AddIf(scene.DurationMs <= 0, $"{path}.durationMs must be positive.", errors);
            expectedStart = End(scene.StartMs, scene.DurationMs);

            ValidateTransition(specification.Scenes, index, errors);
            var effectiveMediaId = ValidateVisualSource(input, scene.VisualSource, path, errors);
            ValidateMotion(scene.Motion, effectiveMediaId, media, input.AspectRatio, path, errors);

            foreach (var overlay in scene.TextOverlays)
            {
                AddIf(string.IsNullOrWhiteSpace(overlay.Id) || !overlayIds.Add(overlay.Id),
                    $"{path}.textOverlays contains an empty or duplicate ID.", errors);
                AddIf(overlay.StartOffsetMs < 0 || overlay.DurationMs <= 0
                    || End(overlay.StartOffsetMs, overlay.DurationMs) > scene.DurationMs,
                    $"{path}.textOverlays timing must fit the scene.", errors);
                AddIf(!IsInside(overlay.Box, specification.SafeZone),
                    $"{path}.textOverlays box must fit the safe zone.", errors);
                ValidateGroundedText(overlay.Text, overlay.GroundingKey, input.FactBindings,
                    $"{path}.textOverlays", errors);
            }

            foreach (var logo in scene.LogoOverlays)
            {
                AddIf(!input.ApprovedBrandAssetIds.Contains(logo.AssetId)
                    || (logo.AssetId != input.Brand.Logo && logo.AssetId != input.Brand.SecondaryLogo),
                    $"{path}.logoOverlays references an unapproved asset.", errors);
                AddIf(logo.StartOffsetMs < 0 || logo.DurationMs <= 0
                    || End(logo.StartOffsetMs, logo.DurationMs) > scene.DurationMs,
                    $"{path}.logoOverlays timing must fit the scene.", errors);
                AddIf(logo.Opacity is < 0 or > 1, $"{path}.logoOverlays opacity is invalid.", errors);
                AddIf(!IsInside(logo.Box, specification.SafeZone),
                    $"{path}.logoOverlays box must fit the safe zone.", errors);
            }
        }

        AddIf(expectedStart != (int)specification.RequestedDuration * 1_000,
            "scenes must end at the exact requested duration.", errors);

        var lastScene = specification.Scenes[^1];
        AddIf(!lastScene.TextOverlays.Any(overlay => overlay.StyleToken == TextOverlayStyle.ClosingCta
            && overlay.Text == specification.CallToAction.Text
            && overlay.GroundingKey == specification.CallToAction.GroundingKey),
            "The last scene must contain the exact closing CTA.", errors);
    }

    private static void ValidateTransition(IReadOnlyList<VideoScene> scenes, int index, List<string> errors)
    {
        var transition = scenes[index].TransitionIn;
        var path = $"scenes[{index}].transitionIn";
        if (index == 0)
        {
            AddIf(transition.Type != TransitionKind.Cut || transition.DurationMs != 0,
                "The first scene must start with a zero-duration cut.", errors);
            return;
        }

        if (transition.Type == TransitionKind.Cut)
        {
            AddIf(transition.DurationMs != 0, $"{path} cut duration must be zero.", errors);
            return;
        }

        var maximum = Math.Min(scenes[index - 1].DurationMs, scenes[index].DurationMs) / 3;
        AddIf(transition.DurationMs is < 1 or > 1_500 || transition.DurationMs > maximum,
            $"{path} duration is outside the supported bound.", errors);
    }

    private static Guid? ValidateVisualSource(
        VideoDirectionRequest input,
        VisualSource source,
        string path,
        List<string> errors)
    {
        Guid? mediaId;
        switch (source.Kind)
        {
            case VisualSourceKind.PropertyMedia:
                AddIf(source.PropertyMediaId is null || source.GeneratedClipId is not null
                    || source.FallbackPropertyMediaId is not null || source.GenerationInstruction is not null,
                    $"{path}.visualSource has invalid propertyMedia fields.", errors);
                mediaId = source.PropertyMediaId;
                break;
            case VisualSourceKind.GeneratedClip:
                AddIf(source.PropertyMediaId is not null || source.GeneratedClipId is null
                    || source.FallbackPropertyMediaId is null || source.GenerationInstruction is not null,
                    $"{path}.visualSource has invalid generatedClip fields.", errors);
                AddIf(source.GeneratedClipId is { } clipId && !input.ApprovedGeneratedClipIds.Contains(clipId),
                    $"{path}.visualSource references an unapproved generated clip.", errors);
                mediaId = source.FallbackPropertyMediaId;
                break;
            case VisualSourceKind.GenerativeMotionRequest:
                AddIf(source.PropertyMediaId is null || source.GeneratedClipId is not null
                    || source.FallbackPropertyMediaId != source.PropertyMediaId || !IsCameraInstruction(source.GenerationInstruction),
                    $"{path}.visualSource has invalid generativeMotionRequest fields.", errors);
                mediaId = source.FallbackPropertyMediaId;
                break;
            default:
                errors.Add($"{path}.visualSource kind is not supported.");
                return null;
        }

        AddIf(mediaId is not { } id || input.Media.All(item => item.MediaId != id),
            $"{path}.visualSource references media outside the authoritative set.", errors);
        if (source.PropertyMediaId is { } propertyMediaId)
        {
            AddIf(input.Media.All(item => item.MediaId != propertyMediaId),
                $"{path}.visualSource references unknown property media.", errors);
        }

        return mediaId;
    }

    private static void ValidateMotion(
        MotionPlan motion,
        Guid? mediaId,
        Dictionary<Guid, VideoMediaInput> media,
        VideoAspectRatio aspectRatio,
        string path,
        List<string> errors)
    {
        AddIf(!IsNormalized(motion.StartViewport) || !IsNormalized(motion.EndViewport),
            $"{path}.motion viewports must remain inside the source image.", errors);
        if (motion.Type == MotionKind.None)
        {
            AddIf(motion.StartViewport != motion.EndViewport,
                $"{path}.motion none requires identical viewports.", errors);
        }
        else
        {
            var widthRatio = Ratio(motion.StartViewport.Width, motion.EndViewport.Width);
            var heightRatio = Ratio(motion.StartViewport.Height, motion.EndViewport.Height);
            AddIf(widthRatio > 1.2m || heightRatio > 1.2m,
                $"{path}.motion exceeds the 20 percent zoom limit.", errors);
        }

        if (mediaId is { } id && media.TryGetValue(id, out var source))
        {
            ValidateAspectFill(motion.StartViewport, source, aspectRatio, $"{path}.motion.startViewport", errors);
            ValidateAspectFill(motion.EndViewport, source, aspectRatio, $"{path}.motion.endViewport", errors);
        }
    }

    private static void ValidateAspectFill(
        NormalizedRect viewport,
        VideoMediaInput media,
        VideoAspectRatio aspectRatio,
        string path,
        List<string> errors)
    {
        if (media.Width <= 0 || media.Height <= 0)
        {
            errors.Add($"{path} requires positive source dimensions.");
            return;
        }

        if (viewport.Width <= 0 || viewport.Height <= 0)
        {
            return;
        }

        var cropAspect = viewport.Width * media.Width / (viewport.Height * media.Height);
        var expected = aspectRatio == VideoAspectRatio.Landscape16By9 ? 16m / 9m : 9m / 16m;
        AddIf(Math.Abs(cropAspect - expected) > 0.02m,
            $"{path} cannot fill a supported output aspect ratio without stretching.", errors);
    }

    private static void ValidateAudioAndGrounding(
        VideoDirectionRequest input,
        VideoProductionSpecification specification,
        List<string> errors)
    {
        var programDuration = (int)specification.RequestedDuration * 1_000;
        var narrationIds = new HashSet<string>(StringComparer.Ordinal);
        var ordered = specification.Audio.NarrationSegments.OrderBy(segment => segment.StartMs).ToArray();
        long previousEnd = 0;
        foreach (var segment in ordered)
        {
            AddIf(string.IsNullOrWhiteSpace(segment.Id) || !narrationIds.Add(segment.Id),
                "Narration IDs must be non-empty and unique.", errors);
            AddIf(segment.StartMs < 0 || segment.DurationMs <= 0
                || End(segment.StartMs, segment.DurationMs) > programDuration,
                $"Narration segment {segment.Id} is outside the program.", errors);
            AddIf(segment.StartMs < previousEnd, $"Narration segment {segment.Id} overlaps another segment.", errors);
            previousEnd = Math.Max(previousEnd, End(segment.StartMs, segment.DurationMs));
            ValidateGroundedText(segment.Text, segment.GroundingKey, input.FactBindings,
                $"Narration segment {segment.Id}", errors);
        }

        foreach (var scene in specification.Scenes)
        {
            var expected = ordered
                .Where(segment => segment.StartMs < End(scene.StartMs, scene.DurationMs)
                    && End(segment.StartMs, segment.DurationMs) > scene.StartMs)
                .Select(segment => segment.Id)
                .Order(StringComparer.Ordinal);
            AddIf(!scene.NarrationSegmentIds.Order(StringComparer.Ordinal).SequenceEqual(expected),
                $"Scene {scene.SceneNumber} narration references do not match timeline overlap.", errors);
        }

        var music = specification.Audio.Music;
        if (music.AssetId is null)
        {
            AddIf(music.Mood != MusicMood.None || music.StartMs != 0 || music.DurationMs != 0
                || music.FadeInMs != 0 || music.FadeOutMs != 0,
                "A plan without music must use the zero-duration none profile.", errors);
        }
        else
        {
            AddIf(!input.ApprovedMusicAssetIds.Contains(music.AssetId), "Music asset is not approved.", errors);
            AddIf(music.StartMs < 0 || music.DurationMs <= 0 || End(music.StartMs, music.DurationMs) > programDuration,
                "Music timing is outside the program.", errors);
            AddIf(music.FadeInMs < 0 || music.FadeOutMs < 0
                || (long)music.FadeInMs + music.FadeOutMs > music.DurationMs,
                "Music fades do not fit the music interval.", errors);
        }

        ValidateGroundedText(
            specification.CallToAction.Text,
            specification.CallToAction.GroundingKey,
            input.FactBindings,
            "callToAction",
            errors);
    }

    private static void ValidateGroundedText(
        string text,
        string key,
        IReadOnlyList<FactBinding> bindings,
        string path,
        List<string> errors)
    {
        var matches = bindings.Where(binding => binding.Key == key).ToArray();
        AddIf(matches.Length != 1 || matches[0].Value != text,
            $"{path} must exactly match one authoritative fact binding.", errors);
    }

    private static VideoOutputProfile GetOutput(VideoAspectRatio aspectRatio) => aspectRatio switch
    {
        VideoAspectRatio.Landscape16By9 => new(1920, 1080, 30, "h264", "aac", "yuv420p", 48_000, 2),
        VideoAspectRatio.Vertical9By16 => new(1080, 1920, 30, "h264", "aac", "yuv420p", 48_000, 2),
        _ => throw new ArgumentOutOfRangeException(nameof(aspectRatio)),
    };

    private static bool IsNormalized(NormalizedRect box) => box.X >= 0 && box.Y >= 0
        && box.Width > 0 && box.Height > 0 && box.X + box.Width <= 1 && box.Y + box.Height <= 1;

    private static bool IsInside(NormalizedRect box, NormalizedRect container) => IsNormalized(box)
        && box.X >= container.X && box.Y >= container.Y
        && box.X + box.Width <= container.X + container.Width
        && box.Y + box.Height <= container.Y + container.Height;

    private static decimal Ratio(decimal left, decimal right) => left <= 0 || right <= 0
        ? decimal.MaxValue
        : Math.Max(left / right, right / left);

    private static long End(int start, int duration) => (long)start + duration;
    private static bool IsCameraInstruction(string? instruction) => instruction is
        "slow cinematic push forward" or "slow cinematic pull back" or "slow horizontal pan"
        or "Slow camera push while preserving the property image.";

    private static void AddIf(bool condition, string message, List<string> errors)
    {
        if (condition)
        {
            errors.Add(message);
        }
    }
}
