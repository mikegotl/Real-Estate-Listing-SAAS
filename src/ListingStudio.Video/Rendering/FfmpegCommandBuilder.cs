using System.Globalization;
using ListingStudio.Application.Audio;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Videos;

namespace ListingStudio.Video.Rendering;

public static class FfmpegCommandBuilder
{
    private const string VideoEncoder = "libx264";

    public static FfmpegRenderCommand Build(VideoRenderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Specification);
        ArgumentNullException.ThrowIfNull(request.PropertyMedia);
        ValidateOutputPath(request.OutputFilePath);

        var mediaGroups = request.PropertyMedia.GroupBy(asset => asset.PropertyMediaId).ToArray();
        if (mediaGroups.Any(group => group.Count() != 1))
        {
            throw new ArgumentException("Property media assets must have unique IDs.", nameof(request));
        }

        var media = mediaGroups.ToDictionary(group => group.Key, group => group.Single());
        var scenes = request.Specification.Scenes;
        if (scenes.Count == 0)
        {
            throw new ArgumentException("A render requires at least one scene.", nameof(request));
        }

        ValidateOutput(request.Specification);
        ValidateTimeline(request.Specification);
        var sceneAssets = scenes.Select(scene => ResolveAsset(scene, media)).ToArray();
        ValidateMediaViewports(scenes, sceneAssets, request.Specification.Output);
        ValidateNarration(request.Specification, request.Narration);

        var arguments = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning",
            "-nostdin",
            "-n",
        };
        for (var index = 0; index < scenes.Count; index++)
        {
            var extensionMs = index + 1 < scenes.Count
                ? scenes[index + 1].TransitionIn.DurationMs
                : 0;
            arguments.AddRange(
            [
                "-loop", "1",
                "-framerate", request.Specification.Output.FrameRate.ToString(CultureInfo.InvariantCulture),
                "-t", Seconds(scenes[index].DurationMs + extensionMs),
                "-i", sceneAssets[index].FilePath,
            ]);
        }

        if (request.Narration is not null)
        {
            arguments.AddRange(["-i", request.Narration.FilePath]);
        }

        arguments.AddRange(
        [
            "-filter_complex", BuildFilterGraph(request, sceneAssets),
            "-map", "[vout]",
            "-map", "[aout]",
            "-c:v", VideoEncoder,
            "-preset", "medium",
            "-crf", "20",
            "-pix_fmt", "yuv420p",
            "-r", request.Specification.Output.FrameRate.ToString(CultureInfo.InvariantCulture),
            "-c:a", "aac",
            "-b:a", "192k",
            "-ar", request.Specification.Output.SampleRateHz.ToString(CultureInfo.InvariantCulture),
            "-ac", request.Specification.Output.AudioChannels.ToString(CultureInfo.InvariantCulture),
            "-movflags", "+faststart",
            "-t", Seconds((int)request.Specification.RequestedDuration * 1_000),
            "-f", "mp4",
            request.OutputFilePath,
        ]);
        return new FfmpegRenderCommand(arguments);
    }

    private static string BuildFilterGraph(
        VideoRenderRequest request,
        VideoRenderMediaAsset[] assets)
    {
        var specification = request.Specification;
        var filters = new List<string>();
        for (var index = 0; index < specification.Scenes.Count; index++)
        {
            var scene = specification.Scenes[index];
            var extensionMs = index + 1 < specification.Scenes.Count
                ? specification.Scenes[index + 1].TransitionIn.DurationMs
                : 0;
            filters.Add(BuildSceneFilter(index, scene, assets[index], specification.Output, extensionMs));
        }

        var current = "scene0";
        for (var index = 1; index < specification.Scenes.Count; index++)
        {
            var transition = specification.Scenes[index].TransitionIn;
            var next = $"timeline{index}";
            if (transition.Type == TransitionKind.Cut)
            {
                filters.Add($"[{current}][scene{index}]concat=n=2:v=1:a=0[{next}]");
            }
            else
            {
                var name = transition.Type switch
                {
                    TransitionKind.Crossfade => "fade",
                    TransitionKind.DipToBlack => "fadeblack",
                    _ => throw new ArgumentOutOfRangeException(nameof(request), "Unsupported transition type."),
                };
                filters.Add(
                    $"[{current}][scene{index}]xfade=transition={name}:duration={Seconds(transition.DurationMs)}"
                    + $":offset={Seconds(specification.Scenes[index].StartMs)}[{next}]");
            }

            current = next;
        }

        var programMs = (int)specification.RequestedDuration * 1_000;
        filters.Add($"[{current}]trim=duration={Seconds(programMs)},setpts=PTS-STARTPTS[vout]");
        BuildAudioFilters(request, filters, programMs);
        return string.Join(';', filters);
    }

    private static string BuildSceneFilter(
        int index,
        VideoScene scene,
        VideoRenderMediaAsset asset,
        VideoOutputProfile output,
        int extensionMs)
    {
        var durationMs = scene.DurationMs + extensionMs;
        string transform;
        if (scene.Motion.Type == MotionKind.None)
        {
            var crop = CalculateCrop(scene.Motion.StartViewport, asset);
            transform = $"crop={crop.Width}:{crop.Height}:{crop.X}:{crop.Y},"
                + $"scale={output.Width}:{output.Height}:flags=lanczos";
        }
        else if (scene.Motion.Type == MotionKind.KenBurns)
        {
            transform = BuildKenBurns(scene.Motion, output, scene.DurationMs);
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(scene), "Unsupported scene motion type.");
        }

        return $"[{index}:v]{transform},fps={output.FrameRate},format=yuv420p,settb=AVTB,"
            + $"trim=duration={Seconds(durationMs)},setpts=PTS-STARTPTS[scene{index}]";
    }

    private static string BuildKenBurns(MotionPlan motion, VideoOutputProfile output, int sceneDurationMs)
    {
        var frameCount = Math.Max(2, (int)Math.Ceiling(sceneDurationMs / 1_000m * output.FrameRate));
        var rawProgress = $"min(on/{frameCount - 1},1)";
        var progress = motion.Easing == MotionEasing.EaseInOut
            ? $"({rawProgress})*({rawProgress})*(3-2*({rawProgress}))"
            : rawProgress;
        var startZoom = 1m / motion.StartViewport.Width;
        var endZoom = 1m / motion.EndViewport.Width;
        var zoom = $"{Number(startZoom)}+({Number(endZoom - startZoom)})*({progress})";
        var x = $"iw*({Number(motion.StartViewport.X)}+({Number(motion.EndViewport.X - motion.StartViewport.X)})*({progress}))";
        var y = $"ih*({Number(motion.StartViewport.Y)}+({Number(motion.EndViewport.Y - motion.StartViewport.Y)})*({progress}))";
        return $"zoompan=z='{zoom}':x='{x}':y='{y}':d=1:s={output.Width}x{output.Height}:fps={output.FrameRate}";
    }

    private static void BuildAudioFilters(VideoRenderRequest request, List<string> filters, int programMs)
    {
        var segments = request.Specification.Audio.NarrationSegments;
        if (segments.Count == 0)
        {
            filters.Add(
                $"anullsrc=r={request.Specification.Output.SampleRateHz}:cl=stereo,"
                + $"atrim=duration={Seconds(programMs)}[aout]");
            return;
        }

        var narration = request.Narration!;
        var inputIndex = request.Specification.Scenes.Count;
        var segmentLabels = new List<string>(segments.Count);
        if (narration.Timing is null)
        {
            var segment = segments[0];
            filters.Add(
                $"[{inputIndex}:a]atrim=duration={Seconds(segment.DurationMs)},asetpts=PTS-STARTPTS,"
                + $"adelay={segment.StartMs}:all=1[narration0]");
            segmentLabels.Add("narration0");
        }
        else
        {
            var timings = narration.Timing.Segments.ToDictionary(timing => timing.SegmentId, StringComparer.Ordinal);
            var sources = new string[segments.Count];
            if (segments.Count == 1)
            {
                sources[0] = $"{inputIndex}:a";
            }
            else
            {
                var splitLabels = Enumerable.Range(0, segments.Count).Select(index => $"narrationSource{index}").ToArray();
                filters.Add($"[{inputIndex}:a]asplit={segments.Count}{string.Concat(splitLabels.Select(label => $"[{label}]"))}");
                for (var index = 0; index < sources.Length; index++)
                {
                    sources[index] = splitLabels[index];
                }
            }

            for (var index = 0; index < segments.Count; index++)
            {
                var segment = segments[index];
                var timing = timings[segment.Id];
                filters.Add(
                    $"[{sources[index]}]atrim=start={Seconds(timing.StartMs)}:end={Seconds(timing.EndMs)},"
                    + $"asetpts=PTS-STARTPTS,adelay={segment.StartMs}:all=1[narration{index}]");
                segmentLabels.Add($"narration{index}");
            }
        }

        var mixed = "narrationMixed";
        if (segmentLabels.Count == 1)
        {
            filters.Add($"[{segmentLabels[0]}]anull[{mixed}]");
        }
        else
        {
            filters.Add(
                $"{string.Concat(segmentLabels.Select(label => $"[{label}]"))}"
                + $"amix=inputs={segmentLabels.Count}:duration=longest:normalize=0[{mixed}]");
        }

        filters.Add(
            $"[{mixed}]apad,atrim=duration={Seconds(programMs)},"
            + $"aresample={request.Specification.Output.SampleRateHz},aformat=channel_layouts=stereo[aout]");
    }

    private static VideoRenderMediaAsset ResolveAsset(
        VideoScene scene,
        Dictionary<Guid, VideoRenderMediaAsset> media)
    {
        var mediaId = scene.VisualSource.Kind == VisualSourceKind.PropertyMedia
            ? scene.VisualSource.PropertyMediaId
            : scene.VisualSource.FallbackPropertyMediaId;
        if (mediaId is not { } id || !media.TryGetValue(id, out var asset))
        {
            throw new ArgumentException($"Scene {scene.SceneNumber} has no supplied still-image asset.");
        }

        if (asset.Width <= 0 || asset.Height <= 0 || !File.Exists(asset.FilePath))
        {
            throw new ArgumentException($"Scene {scene.SceneNumber} has an invalid still-image asset.");
        }

        return asset;
    }

    private static void ValidateNarration(
        VideoProductionSpecification specification,
        VideoRenderNarrationAsset? narration)
    {
        var segments = specification.Audio.NarrationSegments;
        if (segments.Count == 0)
        {
            return;
        }

        var programMs = (int)specification.RequestedDuration * 1_000;
        var ordered = segments.OrderBy(segment => segment.StartMs).ToArray();
        var previousEnd = 0;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var segment in ordered)
        {
            if (string.IsNullOrWhiteSpace(segment.Id)
                || !ids.Add(segment.Id)
                || segment.StartMs < previousEnd
                || segment.DurationMs <= 0
                || segment.StartMs + segment.DurationMs > programMs)
            {
                throw new ArgumentException("Narration segments have invalid IDs or program timing.");
            }

            previousEnd = segment.StartMs + segment.DurationMs;
        }

        if (narration is null || !File.Exists(narration.FilePath))
        {
            throw new ArgumentException("Narration audio is required by the production specification.");
        }

        if (narration.Timing is null)
        {
            if (segments.Count != 1)
            {
                throw new ArgumentException("Multiple narration segments require measured timing metadata.");
            }

            return;
        }

        var expected = segments.Select(segment => segment.Id).Order(StringComparer.Ordinal).ToArray();
        var actual = narration.Timing.Segments.Select(segment => segment.SegmentId).Order(StringComparer.Ordinal).ToArray();
        if (actual.Distinct(StringComparer.Ordinal).Count() != actual.Length
            || !expected.SequenceEqual(actual, StringComparer.Ordinal)
            || narration.Timing.Segments.Any(timing => timing.StartMs < 0 || timing.EndMs <= timing.StartMs))
        {
            throw new ArgumentException("Narration timing does not match the production specification.");
        }
    }

    private static void ValidateTimeline(VideoProductionSpecification specification)
    {
        var expectedStart = 0;
        for (var index = 0; index < specification.Scenes.Count; index++)
        {
            var scene = specification.Scenes[index];
            if (scene.StartMs != expectedStart || scene.DurationMs <= 0)
            {
                throw new ArgumentException("Scene timing must be positive and contiguous.");
            }

            var transition = scene.TransitionIn;
            var transitionValid = index == 0
                ? transition.Type == TransitionKind.Cut && transition.DurationMs == 0
                : transition.Type == TransitionKind.Cut
                    ? transition.DurationMs == 0
                    : transition.Type is TransitionKind.Crossfade or TransitionKind.DipToBlack
                        && transition.DurationMs is >= 1 and <= 1_500
                        && transition.DurationMs <= Math.Min(
                            specification.Scenes[index - 1].DurationMs,
                            scene.DurationMs) / 3;
            if (!transitionValid)
            {
                throw new ArgumentException($"Scene {scene.SceneNumber} has an unsupported transition.");
            }

            if (!IsViewport(scene.Motion.StartViewport)
                || !IsViewport(scene.Motion.EndViewport)
                || scene.Motion.Type == MotionKind.None && scene.Motion.StartViewport != scene.Motion.EndViewport
                || scene.Motion.Type is not (MotionKind.None or MotionKind.KenBurns))
            {
                throw new ArgumentException($"Scene {scene.SceneNumber} has invalid motion viewports.");
            }

            expectedStart += scene.DurationMs;
        }

        if (expectedStart != (int)specification.RequestedDuration * 1_000)
        {
            throw new ArgumentException("Scene timing must equal the requested program duration.");
        }
    }

    private static bool IsViewport(NormalizedRect viewport) => viewport.X >= 0
        && viewport.Y >= 0
        && viewport.Width > 0
        && viewport.Height > 0
        && viewport.X + viewport.Width <= 1
        && viewport.Y + viewport.Height <= 1;

    private static void ValidateMediaViewports(
        IReadOnlyList<VideoScene> scenes,
        VideoRenderMediaAsset[] assets,
        VideoOutputProfile output)
    {
        var outputAspect = (decimal)output.Width / output.Height;
        for (var index = 0; index < scenes.Count; index++)
        {
            ValidateViewportAspect(scenes[index], assets[index], scenes[index].Motion.StartViewport, outputAspect);
            ValidateViewportAspect(scenes[index], assets[index], scenes[index].Motion.EndViewport, outputAspect);
        }
    }

    private static void ValidateViewportAspect(
        VideoScene scene,
        VideoRenderMediaAsset asset,
        NormalizedRect viewport,
        decimal outputAspect)
    {
        var viewportAspect = viewport.Width * asset.Width / (viewport.Height * asset.Height);
        if (Math.Abs(viewportAspect - outputAspect) > 0.02m)
        {
            throw new ArgumentException(
                $"Scene {scene.SceneNumber} viewport does not match its media or output aspect ratio.");
        }
    }

    private static void ValidateOutput(VideoProductionSpecification specification)
    {
        var output = specification.Output;
        if (specification.AspectRatio != VideoAspectRatio.Landscape16By9
            || output.Width != 1_920
            || output.Height != 1_080
            || output.FrameRate != 30
            || !string.Equals(output.VideoCodec, "h264", StringComparison.Ordinal)
            || !string.Equals(output.AudioCodec, "aac", StringComparison.Ordinal)
            || !string.Equals(output.PixelFormat, "yuv420p", StringComparison.Ordinal)
            || output.SampleRateHz != 48_000
            || output.AudioChannels != 2)
        {
            throw new NotSupportedException("The initial renderer supports only the 1920x1080 H.264/AAC output profile.");
        }

    }

    private static void ValidateOutputPath(string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (!string.Equals(Path.GetExtension(outputPath), ".mp4", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The renderer output path must use the .mp4 extension.", nameof(outputPath));
        }

        if (File.Exists(outputPath))
        {
            throw new IOException("The renderer will not overwrite an existing output file.");
        }
    }

    private static (int X, int Y, int Width, int Height) CalculateCrop(
        NormalizedRect viewport,
        VideoRenderMediaAsset asset)
    {
        var width = Math.Max(1, Math.Min(asset.Width, (int)Math.Round(asset.Width * viewport.Width)));
        var height = Math.Max(1, Math.Min(asset.Height, (int)Math.Round(asset.Height * viewport.Height)));
        var x = Math.Clamp((int)Math.Round(asset.Width * viewport.X), 0, asset.Width - width);
        var y = Math.Clamp((int)Math.Round(asset.Height * viewport.Y), 0, asset.Height - height);
        return (x, y, width, height);
    }

    private static string Seconds(int milliseconds) => Number(milliseconds / 1_000m);

    private static string Number(decimal value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
