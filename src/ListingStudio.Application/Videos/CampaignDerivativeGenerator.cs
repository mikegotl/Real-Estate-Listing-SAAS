using ListingStudio.Domain.Videos;

namespace ListingStudio.Application.Videos;

public sealed class CampaignDerivativeGenerator : ICampaignDerivativeGenerator
{
    private const int MinimumSceneDurationMs = 2_000;

    private static readonly (CampaignDeliverableKind Kind, RequestedDuration Duration)[] Deliverables =
    [
        (CampaignDeliverableKind.Hero, RequestedDuration.Hero60),
        (CampaignDeliverableKind.Feature, RequestedDuration.Feature30),
        (CampaignDeliverableKind.Teaser, RequestedDuration.Teaser15),
    ];

    public CampaignDerivativeSet Generate(CampaignDerivativeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.MasterSpecification);
        ArgumentNullException.ThrowIfNull(request.PropertyMedia);
        ValidateMaster(request.MasterSpecification);
        var groupedMedia = request.PropertyMedia.GroupBy(item => item.PropertyMediaId).ToArray();
        if (groupedMedia.Any(group => group.Count() != 1)
            || request.PropertyMedia.Any(item => item.Width <= 0 || item.Height <= 0))
        {
            throw new ArgumentException("Derivative media must have unique IDs and positive dimensions.", nameof(request));
        }

        var media = groupedMedia.ToDictionary(group => group.Key, group => group.First());
        var groupedVideos = (request.PropertyVideos ?? []).GroupBy(item => item.PropertyVideoId).ToArray();
        if (groupedVideos.Any(group => group.Count() != 1)
            || (request.PropertyVideos ?? []).Any(item => item.Width <= 0 || item.Height <= 0 || item.DurationMs <= 0))
        {
            throw new ArgumentException("Derivative property videos must have unique IDs and positive metadata.", nameof(request));
        }

        var videos = groupedVideos.ToDictionary(group => group.Key, group => group.First());

        var derivatives = new List<CampaignDerivative>(6);
        foreach (var aspectRatio in Enum.GetValues<VideoAspectRatio>())
        {
            foreach (var (kind, duration) in Deliverables)
            {
                derivatives.Add(CreateDerivative(request.MasterSpecification, media, videos, kind, duration, aspectRatio));
            }
        }

        return new CampaignDerivativeSet(derivatives);
    }

    private static CampaignDerivative CreateDerivative(
        VideoProductionSpecification master,
        Dictionary<Guid, CampaignDerivativeMedia> media,
        Dictionary<Guid, CampaignDerivativeVideo> videos,
        CampaignDeliverableKind kind,
        RequestedDuration duration,
        VideoAspectRatio aspectRatio)
    {
        var selected = SelectScenes(master.Scenes, duration);
        var allocatedDurations = AllocateDurations(selected, (int)duration * 1_000);
        var safeZone = SafeZoneFor(aspectRatio);
        var output = OutputFor(aspectRatio);
        var scenes = new List<VideoScene>(selected.Length);
        var startMs = 0;
        for (var index = 0; index < selected.Length; index++)
        {
            var source = selected[index];
            var sceneDuration = allocatedDurations[index];
            CampaignDerivativeMedia dimensions;
            if (source.VisualSource.Kind == VisualSourceKind.PropertyVideo)
            {
                if (source.VisualSource.PropertyVideoId is not { } videoId
                    || !videos.TryGetValue(videoId, out var video)
                    || source.VisualSource.PropertyVideoStartMs is not { } videoStartMs
                    || (long)videoStartMs + sceneDuration > video.DurationMs)
                {
                    throw new ArgumentException($"Master scene {source.SceneNumber} references an unavailable property video window.");
                }

                dimensions = new CampaignDerivativeMedia(video.PropertyVideoId, video.Width, video.Height);
            }
            else
            {
                var mediaId = EffectiveMediaId(source.VisualSource)
                    ?? throw new ArgumentException($"Master scene {source.SceneNumber} has no property-media fallback.");
                if (!media.TryGetValue(mediaId, out var suppliedDimensions))
                {
                    throw new ArgumentException($"Master scene {source.SceneNumber} references unsupplied property media.");
                }

                dimensions = suppliedDimensions;
            }

            var transition = index == 0
                ? new TransitionPlan(TransitionKind.Cut, 0)
                : RetimingTransition(source.TransitionIn, allocatedDurations[index - 1], sceneDuration);
            scenes.Add(new VideoScene(
                index + 1,
                startMs,
                sceneDuration,
                source.VisualSource,
                transition,
                ReframeMotion(source.Motion, dimensions, output),
                source.TextOverlays.Select(overlay => RetimingOverlay(
                    overlay, source.DurationMs, sceneDuration, master.SafeZone, safeZone)).ToArray(),
                source.LogoOverlays.Select(overlay => RetimingLogo(
                    overlay, source.DurationMs, sceneDuration, master.SafeZone, safeZone)).ToArray(),
                []));
            startMs += sceneDuration;
        }

        var narration = RetimingNarration(master, selected, scenes);
        for (var index = 0; index < scenes.Count; index++)
        {
            var scene = scenes[index];
            var ids = narration
                .Where(segment => segment.StartMs < scene.StartMs + scene.DurationMs
                    && segment.StartMs + segment.DurationMs > scene.StartMs)
                .Select(segment => segment.Id)
                .Order(StringComparer.Ordinal)
                .ToArray();
            scenes[index] = scene with { NarrationSegmentIds = ids };
        }

        var music = RetimingMusic(master.Audio.Music, (int)duration * 1_000);
        var specification = master with
        {
            RequestedDuration = duration,
            AspectRatio = aspectRatio,
            Output = output,
            SafeZone = safeZone,
            Audio = new AudioPlan(narration, music),
            Scenes = scenes,
        };
        ValidateDerivative(specification, media, videos);
        return new CampaignDerivative(
            kind,
            aspectRatio,
            specification,
            scenes.Select(scene => EffectiveMediaId(scene.VisualSource)).OfType<Guid>().ToHashSet(),
            scenes.Select(scene => scene.VisualSource.GeneratedClipId).OfType<Guid>().ToHashSet(),
            scenes.Select(scene => scene.VisualSource.PropertyVideoId).OfType<Guid>().ToHashSet());
    }

    private static VideoScene[] SelectScenes(IReadOnlyList<VideoScene> scenes, RequestedDuration duration)
    {
        if (duration == RequestedDuration.Hero60)
        {
            return scenes.ToArray();
        }

        var requestedCount = duration == RequestedDuration.Feature30 ? 6 : 3;
        var count = Math.Min(scenes.Count, requestedCount);
        if (count == scenes.Count)
        {
            return scenes.ToArray();
        }

        var selected = new HashSet<int> { 0, scenes.Count - 1 };
        foreach (var candidate in Enumerable.Range(1, scenes.Count - 2)
            .OrderByDescending(index => scenes[index].VisualSource.GeneratedClipId is not null)
            .ThenByDescending(index => scenes[index].TextOverlays.Count + scenes[index].LogoOverlays.Count)
            .ThenByDescending(index => scenes[index].DurationMs)
            .ThenBy(index => index))
        {
            if (selected.Count >= count)
            {
                break;
            }

            selected.Add(candidate);
        }

        return selected.Order().Select(index => scenes[index]).ToArray();
    }

    private static int[] AllocateDurations(VideoScene[] scenes, int targetMs)
    {
        var minimum = Math.Min(MinimumSceneDurationMs, targetMs / scenes.Length);
        var allocated = Enumerable.Repeat(minimum, scenes.Length).ToArray();
        var remaining = targetMs - allocated.Sum();
        var totalWeight = scenes.Sum(scene => (long)scene.DurationMs);
        var fractions = new List<(int Index, decimal Fraction)>();
        var distributed = 0;
        for (var index = 0; index < scenes.Length; index++)
        {
            var exact = remaining * (decimal)scenes[index].DurationMs / totalWeight;
            var whole = (int)Math.Floor(exact);
            allocated[index] += whole;
            distributed += whole;
            fractions.Add((index, exact - whole));
        }

        foreach (var item in fractions.OrderByDescending(item => item.Fraction).ThenBy(item => item.Index)
            .Take(remaining - distributed))
        {
            allocated[item.Index]++;
        }

        return allocated;
    }

    private static List<NarrationSegment> RetimingNarration(
        VideoProductionSpecification master,
        VideoScene[] selected,
        List<VideoScene> derivatives)
    {
        var result = new List<NarrationSegment>();
        var previousEnd = 0;
        foreach (var segment in master.Audio.NarrationSegments.OrderBy(segment => segment.StartMs))
        {
            var mappedIntervals = new List<(int StartMs, int EndMs)>();
            for (var index = 0; index < selected.Length; index++)
            {
                var sourceScene = selected[index];
                if (!sourceScene.NarrationSegmentIds.Contains(segment.Id, StringComparer.Ordinal))
                {
                    continue;
                }

                var sourceEnd = sourceScene.StartMs + sourceScene.DurationMs;
                var overlapStart = Math.Max(segment.StartMs, sourceScene.StartMs);
                var overlapEnd = Math.Min(segment.StartMs + segment.DurationMs, sourceEnd);
                if (overlapEnd <= overlapStart)
                {
                    continue;
                }

                var targetScene = derivatives[index];
                var ratio = targetScene.DurationMs / (decimal)sourceScene.DurationMs;
                var mappedStart = targetScene.StartMs
                    + (int)Math.Round((overlapStart - sourceScene.StartMs) * ratio);
                var mappedEnd = targetScene.StartMs
                    + (int)Math.Round((overlapEnd - sourceScene.StartMs) * ratio);
                mappedIntervals.Add((mappedStart, mappedEnd));
            }

            if (mappedIntervals.Count == 0)
            {
                continue;
            }

            var start = Math.Max(previousEnd, mappedIntervals.Min(interval => interval.StartMs));
            var end = mappedIntervals.Max(interval => interval.EndMs);
            if (end <= start)
            {
                continue;
            }

            result.Add(segment with { StartMs = start, DurationMs = end - start });
            previousEnd = end;
        }

        return result;
    }

    private static TransitionPlan RetimingTransition(TransitionPlan source, int previousDuration, int duration)
    {
        if (source.Type == TransitionKind.Cut)
        {
            return new TransitionPlan(TransitionKind.Cut, 0);
        }

        var maximum = Math.Min(1_500, Math.Min(previousDuration, duration) / 3);
        return maximum < 1
            ? new TransitionPlan(TransitionKind.Cut, 0)
            : source with { DurationMs = Math.Min(source.DurationMs, maximum) };
    }

    private static MotionPlan ReframeMotion(
        MotionPlan motion,
        CampaignDerivativeMedia media,
        VideoOutputProfile output)
    {
        var start = ReframeViewport(motion.StartViewport, media, output);
        var end = motion.Type == MotionKind.None ? start : ReframeViewport(motion.EndViewport, media, output);
        return motion with { StartViewport = start, EndViewport = end };
    }

    private static NormalizedRect ReframeViewport(
        NormalizedRect source,
        CampaignDerivativeMedia media,
        VideoOutputProfile output)
    {
        var normalizedAspect = (decimal)output.Width / output.Height * media.Height / media.Width;
        var width = source.Width;
        var height = source.Height;
        if (width / height > normalizedAspect)
        {
            width = height * normalizedAspect;
        }
        else
        {
            height = width / normalizedAspect;
        }

        var centerX = source.X + source.Width / 2;
        var centerY = source.Y + source.Height / 2;
        var x = Math.Clamp(centerX - width / 2, source.X, source.X + source.Width - width);
        var y = Math.Clamp(centerY - height / 2, source.Y, source.Y + source.Height - height);
        return new NormalizedRect(x, y, width, height);
    }

    private static TextOverlay RetimingOverlay(
        TextOverlay overlay,
        int sourceDuration,
        int targetDuration,
        NormalizedRect sourceSafeZone,
        NormalizedRect targetSafeZone)
    {
        var (start, duration) = RetimingInterval(
            overlay.StartOffsetMs, overlay.DurationMs, sourceDuration, targetDuration);
        return overlay with
        {
            StartOffsetMs = start,
            DurationMs = duration,
            Box = RemapBox(overlay.Box, sourceSafeZone, targetSafeZone),
        };
    }

    private static LogoOverlay RetimingLogo(
        LogoOverlay overlay,
        int sourceDuration,
        int targetDuration,
        NormalizedRect sourceSafeZone,
        NormalizedRect targetSafeZone)
    {
        var (start, duration) = RetimingInterval(
            overlay.StartOffsetMs, overlay.DurationMs, sourceDuration, targetDuration);
        return overlay with
        {
            StartOffsetMs = start,
            DurationMs = duration,
            Box = RemapBox(overlay.Box, sourceSafeZone, targetSafeZone),
        };
    }

    private static (int StartMs, int DurationMs) RetimingInterval(
        int startMs,
        int durationMs,
        int sourceDuration,
        int targetDuration)
    {
        var ratio = targetDuration / (decimal)sourceDuration;
        var start = Math.Clamp((int)Math.Round(startMs * ratio), 0, targetDuration - 1);
        var duration = Math.Clamp((int)Math.Round(durationMs * ratio), 1, targetDuration - start);
        return (start, duration);
    }

    private static NormalizedRect RemapBox(
        NormalizedRect box,
        NormalizedRect source,
        NormalizedRect target)
    {
        var relativeX = (box.X - source.X) / source.Width;
        var relativeY = (box.Y - source.Y) / source.Height;
        var width = Math.Min(target.Width, box.Width / source.Width * target.Width);
        var height = Math.Min(target.Height, box.Height / source.Height * target.Height);
        var x = Math.Clamp(target.X + relativeX * target.Width, target.X, target.X + target.Width - width);
        var y = Math.Clamp(target.Y + relativeY * target.Height, target.Y, target.Y + target.Height - height);
        return new NormalizedRect(x, y, width, height);
    }

    private static MusicPlan RetimingMusic(MusicPlan source, int targetMs)
    {
        if (source.AssetId is null)
        {
            return new MusicPlan(null, MusicMood.None, 0, 0, 0, 0, 0, 0);
        }

        return source with
        {
            StartMs = 0,
            DurationMs = targetMs,
            FadeInMs = Math.Min(source.FadeInMs, targetMs / 2),
            FadeOutMs = Math.Min(source.FadeOutMs, targetMs / 2),
        };
    }

    private static void ValidateMaster(VideoProductionSpecification master)
    {
        var expectedStart = 0;
        var hasContiguousScenes = master.Scenes.Count >= 2;
        for (var index = 0; index < master.Scenes.Count && hasContiguousScenes; index++)
        {
            var scene = master.Scenes[index];
            hasContiguousScenes = scene.SceneNumber == index + 1
                && scene.StartMs == expectedStart
                && scene.DurationMs > 0;
            expectedStart += scene.DurationMs;
        }

        var hasClosingCta = master.Scenes.Count > 0 && master.Scenes[^1].TextOverlays.Any(overlay =>
            overlay.StyleToken == TextOverlayStyle.ClosingCta
            && overlay.Text == master.CallToAction.Text
            && overlay.GroundingKey == master.CallToAction.GroundingKey);
        if (master.RequestedDuration != RequestedDuration.Hero60
            || !hasContiguousScenes
            || expectedStart != 60_000
            || !hasClosingCta)
        {
            throw new ArgumentException("Campaign derivatives require a contiguous 60-second master specification.", nameof(master));
        }
    }

    private static void ValidateDerivative(
        VideoProductionSpecification specification,
        Dictionary<Guid, CampaignDerivativeMedia> media,
        Dictionary<Guid, CampaignDerivativeVideo> videos)
    {
        var expectedStart = 0;
        for (var index = 0; index < specification.Scenes.Count; index++)
        {
            var scene = specification.Scenes[index];
            if (scene.StartMs != expectedStart || scene.DurationMs <= 0)
            {
                throw new InvalidOperationException("Generated derivative scene timing is invalid.");
            }

            CampaignDerivativeMedia dimensions;
            if (scene.VisualSource.Kind == VisualSourceKind.PropertyVideo)
            {
                var videoId = scene.VisualSource.PropertyVideoId
                    ?? throw new InvalidOperationException("Generated derivative lost its property-video source.");
                var requiredDurationMs = scene.DurationMs
                    + (index + 1 < specification.Scenes.Count
                        ? specification.Scenes[index + 1].TransitionIn.DurationMs
                        : 0);
                if (!videos.TryGetValue(videoId, out var video)
                    || scene.VisualSource.PropertyVideoStartMs is not { } startMs
                    || (long)startMs + requiredDurationMs > video.DurationMs)
                {
                    throw new InvalidOperationException("Generated derivative has an invalid property-video window.");
                }

                dimensions = new CampaignDerivativeMedia(videoId, video.Width, video.Height);
            }
            else
            {
                var id = EffectiveMediaId(scene.VisualSource)
                    ?? throw new InvalidOperationException("Generated derivative lost its property-media fallback.");
                dimensions = media[id];
            }
            var expectedAspect = (decimal)specification.Output.Width / specification.Output.Height;
            if (!HasAspect(scene.Motion.StartViewport, dimensions, expectedAspect)
                || !HasAspect(scene.Motion.EndViewport, dimensions, expectedAspect)
                || scene.TextOverlays.Any(item => !IsInside(item.Box, specification.SafeZone))
                || scene.LogoOverlays.Any(item => !IsInside(item.Box, specification.SafeZone)))
            {
                throw new InvalidOperationException("Generated derivative layout is outside supported bounds.");
            }

            expectedStart += scene.DurationMs;
        }

        if (expectedStart != (int)specification.RequestedDuration * 1_000
            || !specification.Scenes[^1].TextOverlays.Any(overlay =>
                overlay.StyleToken == TextOverlayStyle.ClosingCta
                && overlay.Text == specification.CallToAction.Text
                && overlay.GroundingKey == specification.CallToAction.GroundingKey))
        {
            throw new InvalidOperationException("Generated derivative is incomplete or missing its closing CTA.");
        }
    }

    private static Guid? EffectiveMediaId(VisualSource source) => source.Kind == VisualSourceKind.PropertyMedia
        ? source.PropertyMediaId
        : source.FallbackPropertyMediaId;

    private static bool IsInside(NormalizedRect box, NormalizedRect container) => box.X >= container.X
        && box.Y >= container.Y
        && box.Width > 0
        && box.Height > 0
        && box.X + box.Width <= container.X + container.Width
        && box.Y + box.Height <= container.Y + container.Height;

    private static bool HasAspect(
        NormalizedRect viewport,
        CampaignDerivativeMedia media,
        decimal expectedAspect)
    {
        var actualAspect = viewport.Width * media.Width / (viewport.Height * media.Height);
        return Math.Abs(actualAspect - expectedAspect) <= 0.02m;
    }

    private static VideoOutputProfile OutputFor(VideoAspectRatio aspectRatio) => aspectRatio switch
    {
        VideoAspectRatio.Landscape16By9 => new(1_920, 1_080, 30, "h264", "aac", "yuv420p", 48_000, 2),
        VideoAspectRatio.Vertical9By16 => new(1_080, 1_920, 30, "h264", "aac", "yuv420p", 48_000, 2),
        _ => throw new ArgumentOutOfRangeException(nameof(aspectRatio)),
    };

    private static NormalizedRect SafeZoneFor(VideoAspectRatio aspectRatio) => aspectRatio switch
    {
        VideoAspectRatio.Landscape16By9 => new(0.05m, 0.05m, 0.9m, 0.9m),
        VideoAspectRatio.Vertical9By16 => new(0.075m, 0.05m, 0.85m, 0.9m),
        _ => throw new ArgumentOutOfRangeException(nameof(aspectRatio)),
    };
}
