using System.Globalization;
using ListingStudio.Application.Audio;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Videos;
using ListingStudio.Video.Configuration;

namespace ListingStudio.Video.Rendering;

public static class FfmpegCommandBuilder
{
    private const string VideoEncoder = "libx264";

    public static FfmpegRenderCommand Build(
        VideoRenderRequest request,
        VideoBrandingTemplateOptions? brandingTemplate = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Specification);
        ArgumentNullException.ThrowIfNull(request.PropertyMedia);
        ArgumentNullException.ThrowIfNull(request.BrandAssets);
        brandingTemplate ??= new VideoBrandingTemplateOptions();
        ValidateOutputPath(request.OutputFilePath);

        var mediaGroups = request.PropertyMedia.GroupBy(asset => asset.PropertyMediaId).ToArray();
        if (mediaGroups.Any(group => group.Count() != 1))
        {
            throw new ArgumentException("Property media assets must have unique IDs.", nameof(request));
        }

        var media = mediaGroups.ToDictionary(group => group.Key, group => group.Single());
        var clipGroups = (request.GeneratedClips ?? []).GroupBy(asset => asset.GeneratedClipId).ToArray();
        if (clipGroups.Any(group => group.Count() != 1))
        {
            throw new ArgumentException("Generated video assets must have unique IDs.", nameof(request));
        }

        var clips = clipGroups.ToDictionary(group => group.Key, group => group.Single());
        var propertyVideoGroups = (request.PropertyVideos ?? [])
            .GroupBy(asset => asset.PropertyVideoId)
            .ToArray();
        if (propertyVideoGroups.Any(group => group.Count() != 1))
        {
            throw new ArgumentException("Property video assets must have unique IDs.", nameof(request));
        }

        var propertyVideos = propertyVideoGroups.ToDictionary(group => group.Key, group => group.Single());
        var neighborhoodGroups = (request.NeighborhoodAssets ?? [])
            .GroupBy(asset => asset.NeighborhoodInsightId)
            .ToArray();
        if (neighborhoodGroups.Any(group => group.Count() != 1))
        {
            throw new ArgumentException("Neighborhood visual assets must have unique IDs.", nameof(request));
        }

        var neighborhoodAssets = neighborhoodGroups.ToDictionary(group => group.Key, group => group.Single());
        var scenes = request.Specification.Scenes;
        if (scenes.Count == 0)
        {
            throw new ArgumentException("A render requires at least one scene.", nameof(request));
        }

        ValidateOutput(request.Specification);
        ValidateTimeline(request.Specification);
        var sceneAssets = scenes.Select((scene, index) => ResolveAsset(
            scene,
            media,
            clips,
            propertyVideos,
            scene.DurationMs + (index + 1 < scenes.Count ? scenes[index + 1].TransitionIn.DurationMs : 0)))
            .ToArray();
        ValidateMediaViewports(scenes, sceneAssets, request.Specification.Output);
        ValidateNarration(request.Specification, request.Narration);
        var brandAssets = ValidateBranding(request, brandingTemplate);
        ValidateMusic(request);
        var neighborhoodOverlays = CreateNeighborhoodOverlays(request.Specification, neighborhoodAssets);

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
            if (sceneAssets[index].IsVideo)
            {
                if (sceneAssets[index].IsGeneratedClip)
                {
                    arguments.AddRange(["-stream_loop", "-1"]);
                }

                if (sceneAssets[index].StartMs > 0)
                {
                    arguments.AddRange(["-ss", Seconds(sceneAssets[index].StartMs)]);
                }

                arguments.AddRange(
                    [
                        "-t", Seconds(scenes[index].DurationMs + extensionMs),
                        "-i", sceneAssets[index].FilePath,
                    ]);
            }
            else
            {
                arguments.AddRange(
                [
                    "-loop", "1",
                    "-framerate", request.Specification.Output.FrameRate.ToString(CultureInfo.InvariantCulture),
                    "-t", Seconds(scenes[index].DurationMs + extensionMs),
                    "-i", sceneAssets[index].FilePath,
                ]);
            }
        }

        int? narrationInput = null;
        if (request.Narration is not null)
        {
            narrationInput = scenes.Count;
            arguments.AddRange(["-i", request.Narration.FilePath]);
        }

        int? musicInput = null;
        if (request.Music is not null)
        {
            musicInput = scenes.Count + (narrationInput is null ? 0 : 1);
            arguments.AddRange(["-stream_loop", "-1", "-i", request.Music.FilePath]);
        }

        var nextInput = scenes.Count + (narrationInput is null ? 0 : 1) + (musicInput is null ? 0 : 1);
        var logoInputs = new List<int>(brandAssets.Length);
        var programMs = (int)request.Specification.RequestedDuration * 1_000;
        foreach (var asset in brandAssets)
        {
            logoInputs.Add(nextInput++);
            arguments.AddRange(
            [
                "-loop", "1",
                "-framerate", request.Specification.Output.FrameRate.ToString(CultureInfo.InvariantCulture),
                "-t", Seconds(programMs),
                "-i", asset.FilePath,
            ]);
        }

        var neighborhoodInputs = new List<int>(neighborhoodOverlays.Length);
        foreach (var overlay in neighborhoodOverlays)
        {
            neighborhoodInputs.Add(nextInput++);
            arguments.AddRange(
            [
                "-loop", "1",
                "-framerate", request.Specification.Output.FrameRate.ToString(CultureInfo.InvariantCulture),
                "-t", Seconds(programMs),
                "-i", overlay.Asset.FilePath,
            ]);
        }

        var inputs = new RenderInputIndexes(narrationInput, musicInput, logoInputs, neighborhoodInputs);
        arguments.AddRange(
        [
            "-filter_complex", BuildFilterGraph(
                request,
                sceneAssets,
                inputs,
                brandingTemplate,
                neighborhoodOverlays),
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
        SceneVisualAsset[] assets,
        RenderInputIndexes inputs,
        VideoBrandingTemplateOptions brandingTemplate,
        NeighborhoodOverlay[] neighborhoodOverlays)
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
        filters.Add($"[{current}]trim=duration={Seconds(programMs)},setpts=PTS-STARTPTS[videoTimeline]");
        current = BuildNeighborhoodFilters(
            specification,
            filters,
            inputs.NeighborhoodInputs,
            neighborhoodOverlays,
            "videoTimeline");
        current = BuildBrandingFilters(request, filters, inputs.LogoInputs, brandingTemplate, current);
        var videoFilters = new List<string>();
        if (brandingTemplate.VideoFadeInMs > 0)
        {
            videoFilters.Add($"fade=t=in:st=0:d={Seconds(brandingTemplate.VideoFadeInMs)}");
        }

        if (brandingTemplate.VideoFadeOutMs > 0)
        {
            videoFilters.Add(
                $"fade=t=out:st={Seconds(programMs - brandingTemplate.VideoFadeOutMs)}"
                + $":d={Seconds(brandingTemplate.VideoFadeOutMs)}");
        }

        videoFilters.Add("format=yuv420p");
        filters.Add($"[{current}]{string.Join(',', videoFilters)}[vout]");
        BuildAudioFilters(request, filters, programMs, inputs.NarrationInput, inputs.MusicInput);
        return string.Join(';', filters);
    }

    private static string BuildSceneFilter(
        int index,
        VideoScene scene,
        SceneVisualAsset asset,
        VideoOutputProfile output,
        int extensionMs)
    {
        var durationMs = scene.DurationMs + extensionMs;
        string transform;
        if (asset.IsVideo)
        {
            transform = $"scale={output.Width}:{output.Height}:force_original_aspect_ratio=increase:flags=lanczos,"
                + $"crop={output.Width}:{output.Height}";
        }
        else if (scene.Motion.Type == MotionKind.None)
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

    private static NeighborhoodOverlay[] CreateNeighborhoodOverlays(
        VideoProductionSpecification specification,
        Dictionary<Guid, VideoRenderNeighborhoodAsset> assets)
    {
        var programMs = (int)specification.RequestedDuration * 1_000;
        var result = new List<NeighborhoodOverlay>();
        foreach (var binding in specification.FactBindings.Where(binding =>
                     binding.Source == FactSource.ApprovedNeighborhood
                     && binding.VisualAssetReferenceId is not null))
        {
            if (!assets.TryGetValue(binding.VisualAssetReferenceId!.Value, out var asset))
            {
                continue;
            }

            if (asset.Width <= 0 || asset.Height <= 0 || !File.Exists(asset.FilePath)
                || string.IsNullOrWhiteSpace(asset.Credit))
            {
                throw new ArgumentException($"Neighborhood asset {asset.NeighborhoodInsightId} is invalid.");
            }

            var windows = specification.Scenes
                .SelectMany(scene => scene.TextOverlays
                    .Where(overlay => overlay.GroundingKey == binding.Key)
                    .Select(overlay => (Start: scene.StartMs + overlay.StartOffsetMs, overlay.DurationMs)))
                .Concat(specification.Audio.NarrationSegments
                    .Where(segment => segment.GroundingKey == binding.Key)
                    .Select(segment => (Start: segment.StartMs, DurationMs: segment.DurationMs)))
                .OrderBy(window => window.Start)
                .ToArray();
            if (windows.Length == 0)
            {
                continue;
            }

            var start = Math.Max(0, windows[0].Start - 500);
            var end = Math.Min(programMs, windows[0].Start + windows[0].DurationMs + 500);
            if (end - start < 2_500)
            {
                end = Math.Min(programMs, start + 2_500);
                start = Math.Max(0, end - 2_500);
            }

            result.Add(new NeighborhoodOverlay(asset, start, end - start));
        }

        return result
            .DistinctBy(overlay => overlay.Asset.NeighborhoodInsightId)
            .OrderBy(overlay => overlay.StartMs)
            .ThenBy(overlay => overlay.Asset.NeighborhoodInsightId)
            .ToArray();
    }

    private static string BuildNeighborhoodFilters(
        VideoProductionSpecification specification,
        List<string> filters,
        IReadOnlyList<int> inputs,
        IReadOnlyList<NeighborhoodOverlay> overlays,
        string sourceLabel)
    {
        var output = specification.Output;
        var current = sourceLabel;
        var width = (int)Math.Round(output.Width * 0.82m);
        var height = (int)Math.Round(output.Height * 0.64m);
        var x = (output.Width - width) / 2;
        var y = (int)Math.Round(output.Height * 0.10m);
        for (var index = 0; index < overlays.Count; index++)
        {
            var overlay = overlays[index];
            var visual = $"neighborhoodVisual{index}";
            var shown = $"neighborhoodShown{index}";
            var credited = $"neighborhoodCredited{index}";
            filters.Add(
                $"[{inputs[index]}:v]scale=w={width}:h={height}:force_original_aspect_ratio=decrease:flags=lanczos,"
                + $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:color=0x111111,format=rgba[{visual}]");
            var start = Seconds(overlay.StartMs);
            var end = Seconds(overlay.StartMs + overlay.DurationMs);
            filters.Add(
                $"[{current}][{visual}]overlay=x={x}:y={y}:enable='between(t,{start},{end})':eof_action=repeat[{shown}]");
            filters.Add(
                $"[{shown}]drawtext=font='Sans':text='{EscapeFilterLiteral(overlay.Asset.Credit)}'"
                + $":fontcolor=white:fontsize={Math.Max(22, output.Width / 64)}:box=1:boxcolor=black@0.75:boxborderw=10"
                + $":x={x + 12}:y={y + height}-text_h-12:fix_bounds=1"
                + $":enable='between(t,{start},{end})'[{credited}]");
            current = credited;
        }

        return current;
    }

    private static string BuildBrandingFilters(
        VideoRenderRequest request,
        List<string> filters,
        IReadOnlyList<int> logoInputs,
        VideoBrandingTemplateOptions brandingTemplate,
        string sourceLabel)
    {
        var output = request.Specification.Output;
        var brand = request.Specification.Brand;
        var current = sourceLabel;
        var filterNumber = 0;
        foreach (var (scene, overlay) in request.Specification.Scenes
            .SelectMany(scene => scene.TextOverlays.Select(overlay => (scene, overlay)))
            .OrderBy(item => item.scene.StartMs + item.overlay.StartOffsetMs)
            .ThenBy(item => item.overlay.Id, StringComparer.Ordinal))
        {
            var template = brandingTemplate.For(overlay.StyleToken);
            var box = ToPixelBox(overlay.Box, output);
            var position = TextPosition(overlay.Anchor, box, template.Padding);
            var startMs = scene.StartMs + overlay.StartOffsetMs;
            var endMs = startMs + overlay.DurationMs;
            var next = $"brand{filterNumber++}";
            var font = string.IsNullOrWhiteSpace(brandingTemplate.FontFilePath)
                ? "font='Sans'"
                : $"fontfile='{EscapeFilterLiteral(Path.GetFullPath(brandingTemplate.FontFilePath))}'";
            filters.Add(
                $"[{current}]drawtext={font}:text='{EscapeFilterLiteral(overlay.Text)}'"
                + $":fontcolor={ResolveColor(template.TextColor, brand)}:fontsize={template.FontSize}"
                + $":box=1:boxcolor={ResolveColor(template.BoxColor, brand)}@{Number(template.BoxOpacity)}"
                + $":boxborderw={template.Padding}:x='{position.X}':y='{position.Y}':fix_bounds=1"
                + $":enable='between(t,{Seconds(startMs)},{Seconds(endMs)})'[{next}]");
            current = next;
        }

        var logoOverlays = OrderedLogoOverlays(request.Specification);
        for (var overlayIndex = 0; overlayIndex < logoOverlays.Length; overlayIndex++)
        {
            var (scene, overlay) = logoOverlays[overlayIndex];
            var input = logoInputs[overlayIndex];
            var box = ToPixelBox(overlay.Box, output);
            var logo = $"logo{filterNumber}";
            var next = $"brand{filterNumber++}";
            filters.Add(
                $"[{input}:v]scale=w={box.Width}:h={box.Height}:force_original_aspect_ratio=decrease,"
                + $"format=rgba,colorchannelmixer=aa={Number(overlay.Opacity)}[{logo}]");
            var startMs = scene.StartMs + overlay.StartOffsetMs;
            var endMs = startMs + overlay.DurationMs;
            filters.Add(
                $"[{current}][{logo}]overlay=x={box.X}+({box.Width}-w)/2:y={box.Y}+({box.Height}-h)/2"
                + $":enable='between(t,{Seconds(startMs)},{Seconds(endMs)})':eof_action=repeat[{next}]");
            current = next;
        }

        return current;
    }

    private static void BuildAudioFilters(
        VideoRenderRequest request,
        List<string> filters,
        int programMs,
        int? narrationInput,
        int? musicInput)
    {
        var segments = request.Specification.Audio.NarrationSegments;
        string? narrationLabel = null;
        if (segments.Count > 0)
        {
            narrationLabel = BuildNarrationFilters(request, filters, narrationInput!.Value);
        }

        string? musicLabel = null;
        if (request.Specification.Audio.Music.AssetId is not null)
        {
            musicLabel = BuildMusicFilters(request, filters, musicInput!.Value);
        }

        if (narrationLabel is null && musicLabel is null)
        {
            filters.Add(
                $"anullsrc=r={request.Specification.Output.SampleRateHz}:cl=stereo,"
                + $"atrim=duration={Seconds(programMs)}[aout]");
            return;
        }

        var mixed = narrationLabel ?? musicLabel!;
        if (narrationLabel is not null && musicLabel is not null)
        {
            filters.Add($"[{narrationLabel}][{musicLabel}]amix=inputs=2:duration=longest:normalize=0[audioMixed]");
            mixed = "audioMixed";
        }

        filters.Add(
            $"[{mixed}]apad,atrim=duration={Seconds(programMs)},"
            + $"aresample={request.Specification.Output.SampleRateHz},aformat=channel_layouts=stereo[aout]");
    }

    private static string BuildNarrationFilters(
        VideoRenderRequest request,
        List<string> filters,
        int inputIndex)
    {
        var segments = request.Specification.Audio.NarrationSegments;
        var narration = request.Narration!;
        var segmentLabels = new List<string>(segments.Count);
        if (narration.Timing is null)
        {
            var narrationStartMs = segments.Min(segment => segment.StartMs);
            var availableProgramMs = (int)request.Specification.RequestedDuration * 1_000 - narrationStartMs;
            filters.Add(
                $"[{inputIndex}:a]atrim=duration={Seconds(availableProgramMs)},asetpts=PTS-STARTPTS,"
                + $"adelay={narrationStartMs}:all=1[narration0]");
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

            // Each segment starts with the scene it was planned against. A segment that runs longer than its
            // slot pushes the next one back rather than talking over it.
            var previousEndMs = 0;
            var ordered = segments.Select((segment, index) => (Segment: segment, Index: index))
                .OrderBy(item => item.Segment.StartMs);
            foreach (var (segment, index) in ordered)
            {
                var timing = timings[segment.Id];
                var startMs = Math.Max(segment.StartMs, previousEndMs);
                previousEndMs = startMs + timing.EndMs - timing.StartMs;
                filters.Add(
                    $"[{sources[index]}]atrim=start={Seconds(timing.StartMs)}:end={Seconds(timing.EndMs)},"
                    + $"asetpts=PTS-STARTPTS,adelay={startMs}:all=1[narration{index}]");
                segmentLabels.Add($"narration{index}");
            }
        }

        const string mixed = "narrationMixed";
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

        return mixed;
    }

    private static string BuildMusicFilters(
        VideoRenderRequest request,
        List<string> filters,
        int inputIndex)
    {
        var music = request.Specification.Audio.Music;
        var chain = new List<string>
        {
            $"atrim=duration={Seconds(music.DurationMs)}",
            "asetpts=PTS-STARTPTS",
            $"volume={Number(music.GainDb)}dB",
        };
        if (music.FadeInMs > 0)
        {
            chain.Add($"afade=t=in:st=0:d={Seconds(music.FadeInMs)}");
        }

        if (music.FadeOutMs > 0)
        {
            chain.Add(
                $"afade=t=out:st={Seconds(music.DurationMs - music.FadeOutMs)}:d={Seconds(music.FadeOutMs)}");
        }

        if (music.StartMs > 0)
        {
            chain.Add($"adelay={music.StartMs}:all=1");
        }

        var narration = request.Specification.Audio.NarrationSegments;
        if (narration.Count > 0 && music.DuckingGainDb < 0)
        {
            var duckingIntervals = string.Join(
                '+',
                narration.Select(segment =>
                    $"between(t,{Seconds(segment.StartMs)},{Seconds(segment.StartMs + segment.DurationMs)})"));
            var duckingFactor = (decimal)Math.Pow(10, (double)music.DuckingGainDb / 20d);
            chain.Add($"volume='if(gt({duckingIntervals},0),{Number(duckingFactor)},1)':eval=frame");
        }

        filters.Add($"[{inputIndex}:a]{string.Join(',', chain)}[musicMixed]");
        return "musicMixed";
    }

    private static VideoRenderBrandAsset[] ValidateBranding(
        VideoRenderRequest request,
        VideoBrandingTemplateOptions template)
    {
        var programMs = (int)request.Specification.RequestedDuration * 1_000;
        if (template.VideoFadeInMs < 0
            || template.VideoFadeOutMs < 0
            || template.VideoFadeInMs + template.VideoFadeOutMs > programMs)
        {
            throw new ArgumentException("Video branding fade timing must fit the program.", nameof(template));
        }

        if (!string.IsNullOrWhiteSpace(template.FontFilePath) && !File.Exists(template.FontFilePath))
        {
            throw new ArgumentException("The configured branding font file does not exist.", nameof(template));
        }

        foreach (var style in Enum.GetValues<TextOverlayStyle>())
        {
            var styleTemplate = template.For(style);
            if (styleTemplate.FontSize is < 8 or > 240
                || styleTemplate.Padding is < 0 or > 100
                || styleTemplate.BoxOpacity is < 0 or > 1)
            {
                throw new ArgumentException($"The {style} branding template is outside supported bounds.", nameof(template));
            }
        }

        ValidateBrandColor(request.Specification.Brand.PrimaryColor, nameof(request.Specification.Brand.PrimaryColor));
        ValidateBrandColor(request.Specification.Brand.SecondaryColor, nameof(request.Specification.Brand.SecondaryColor));

        var groups = request.BrandAssets.GroupBy(asset => asset.AssetId, StringComparer.Ordinal).ToArray();
        if (groups.Any(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() != 1))
        {
            throw new ArgumentException("Brand assets must have unique, non-empty IDs.", nameof(request));
        }

        var supplied = groups.ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        var overlayIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scene in request.Specification.Scenes)
        {
            foreach (var overlay in scene.TextOverlays)
            {
                if (string.IsNullOrWhiteSpace(overlay.Id)
                    || !overlayIds.Add(overlay.Id)
                    || string.IsNullOrWhiteSpace(overlay.Text)
                    || overlay.Text.Any(char.IsControl)
                    || overlay.StartOffsetMs < 0
                    || overlay.DurationMs <= 0
                    || (long)overlay.StartOffsetMs + overlay.DurationMs > scene.DurationMs
                    || !IsViewport(overlay.Box))
                {
                    throw new ArgumentException($"Scene {scene.SceneNumber} has an invalid text overlay.", nameof(request));
                }
            }
        }

        var resolved = new List<VideoRenderBrandAsset>();
        foreach (var (scene, overlay) in OrderedLogoOverlays(request.Specification))
        {
            if (overlay.AssetId != request.Specification.Brand.Logo
                && overlay.AssetId != request.Specification.Brand.SecondaryLogo)
            {
                throw new ArgumentException($"Scene {scene.SceneNumber} references a logo outside its BrandKit.", nameof(request));
            }

            if (overlay.StartOffsetMs < 0
                || overlay.DurationMs <= 0
                || (long)overlay.StartOffsetMs + overlay.DurationMs > scene.DurationMs
                || overlay.Opacity is < 0 or > 1
                || !IsViewport(overlay.Box)
                || !supplied.TryGetValue(overlay.AssetId, out var asset)
                || asset.Width <= 0
                || asset.Height <= 0
                || !File.Exists(asset.FilePath))
            {
                throw new ArgumentException($"Scene {scene.SceneNumber} has an invalid or missing logo asset.", nameof(request));
            }

            resolved.Add(asset);
        }

        return resolved.ToArray();
    }

    private static (VideoScene Scene, LogoOverlay Overlay)[] OrderedLogoOverlays(
        VideoProductionSpecification specification) => specification.Scenes
        .SelectMany(scene => scene.LogoOverlays.Select(overlay => (Scene: scene, Overlay: overlay)))
        .OrderBy(item => item.Scene.StartMs + item.Overlay.StartOffsetMs)
        .ThenBy(item => item.Overlay.AssetId, StringComparer.Ordinal)
        .ToArray();

    private static void ValidateMusic(VideoRenderRequest request)
    {
        var plan = request.Specification.Audio.Music;
        if (plan.AssetId is null)
        {
            if (request.Music is not null
                || plan.Mood != MusicMood.None
                || plan.StartMs != 0
                || plan.DurationMs != 0
                || plan.GainDb != 0
                || plan.FadeInMs != 0
                || plan.FadeOutMs != 0
                || plan.DuckingGainDb != 0)
            {
                throw new ArgumentException("A render without planned music cannot receive a music asset or mix settings.", nameof(request));
            }

            return;
        }

        var programMs = (int)request.Specification.RequestedDuration * 1_000;
        if (request.Music is null
            || !string.Equals(request.Music.AssetId, plan.AssetId, StringComparison.Ordinal)
            || !File.Exists(request.Music.FilePath)
            || plan.Mood == MusicMood.None
            || plan.StartMs < 0
            || plan.DurationMs <= 0
            || (long)plan.StartMs + plan.DurationMs > programMs
            || plan.GainDb is < -60 or > 0
            || plan.DuckingGainDb is < -60 or > 0
            || plan.FadeInMs < 0
            || plan.FadeOutMs < 0
            || (long)plan.FadeInMs + plan.FadeOutMs > plan.DurationMs)
        {
            throw new ArgumentException("The supplied music asset or mix plan is invalid.", nameof(request));
        }
    }

    private static PixelBox ToPixelBox(NormalizedRect box, VideoOutputProfile output)
    {
        var x = (int)Math.Round(box.X * output.Width);
        var y = (int)Math.Round(box.Y * output.Height);
        var width = Math.Max(1, (int)Math.Round(box.Width * output.Width));
        var height = Math.Max(1, (int)Math.Round(box.Height * output.Height));
        return new PixelBox(x, y, width, height);
    }

    private static (string X, string Y) TextPosition(OverlayAnchor anchor, PixelBox box, int padding)
    {
        var left = (box.X + padding).ToString(CultureInfo.InvariantCulture);
        var center = $"{box.X}+({box.Width}-text_w)/2";
        var right = $"{box.X + box.Width}-text_w-{padding}";
        var top = (box.Y + padding).ToString(CultureInfo.InvariantCulture);
        var middle = $"{box.Y}+({box.Height}-text_h)/2";
        var bottom = $"{box.Y + box.Height}-text_h-{padding}";
        return anchor switch
        {
            OverlayAnchor.TopLeft => (left, top),
            OverlayAnchor.TopCenter => (center, top),
            OverlayAnchor.TopRight => (right, top),
            OverlayAnchor.Center => (center, middle),
            OverlayAnchor.BottomLeft => (left, bottom),
            OverlayAnchor.BottomCenter => (center, bottom),
            OverlayAnchor.BottomRight => (right, bottom),
            _ => throw new ArgumentOutOfRangeException(nameof(anchor)),
        };
    }

    private static string ResolveColor(BrandColorToken token, BrandKit brand)
    {
        var color = token == BrandColorToken.Primary ? brand.PrimaryColor : brand.SecondaryColor;
        return $"0x{color[1..]}";
    }

    private static void ValidateBrandColor(string color, string parameterName)
    {
        if (color.Length != 7 || color[0] != '#' || color[1..].Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Brand colors must use six-digit hexadecimal notation.", parameterName);
        }
    }

    private static string EscapeFilterLiteral(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("'", "\\'", StringComparison.Ordinal)
        .Replace(":", "\\:", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace(",", "\\,", StringComparison.Ordinal)
        .Replace(";", "\\;", StringComparison.Ordinal)
        .Replace("[", "\\[", StringComparison.Ordinal)
        .Replace("]", "\\]", StringComparison.Ordinal);

    private static SceneVisualAsset ResolveAsset(
        VideoScene scene,
        Dictionary<Guid, VideoRenderMediaAsset> media,
        Dictionary<Guid, VideoRenderGeneratedClipAsset> clips,
        Dictionary<Guid, VideoRenderPropertyVideoAsset> propertyVideos,
        int requiredDurationMs)
    {
        if (scene.VisualSource.Kind == VisualSourceKind.GeneratedClip
            && scene.VisualSource.GeneratedClipId is { } clipId
            && clips.TryGetValue(clipId, out var clip))
        {
            if (clip.Width <= 0 || clip.Height <= 0 || clip.DurationMs < scene.DurationMs
                || !File.Exists(clip.FilePath))
            {
                throw new ArgumentException($"Scene {scene.SceneNumber} has an invalid generated video asset.");
            }

            return new SceneVisualAsset(clip.FilePath, clip.Width, clip.Height, true, true, 0);
        }

        if (scene.VisualSource.Kind == VisualSourceKind.PropertyVideo
            && scene.VisualSource.PropertyVideoId is { } propertyVideoId
            && propertyVideos.TryGetValue(propertyVideoId, out var propertyVideo))
        {
            var startMs = scene.VisualSource.PropertyVideoStartMs ?? 0;
            if (propertyVideo.Width <= 0
                || propertyVideo.Height <= 0
                || propertyVideo.DurationMs <= 0
                || startMs < 0
                || (long)startMs + requiredDurationMs > propertyVideo.DurationMs
                || !File.Exists(propertyVideo.FilePath))
            {
                throw new ArgumentException($"Scene {scene.SceneNumber} has an invalid property video asset or window.");
            }

            return new SceneVisualAsset(
                propertyVideo.FilePath,
                propertyVideo.Width,
                propertyVideo.Height,
                true,
                false,
                startMs);
        }

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

        return new SceneVisualAsset(asset.FilePath, asset.Width, asset.Height, false, false, 0);
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
        SceneVisualAsset[] assets,
        VideoOutputProfile output)
    {
        var outputAspect = (decimal)output.Width / output.Height;
        for (var index = 0; index < scenes.Count; index++)
        {
            if (assets[index].IsVideo)
            {
                continue;
            }

            ValidateViewportAspect(scenes[index], assets[index], scenes[index].Motion.StartViewport, outputAspect);
            ValidateViewportAspect(scenes[index], assets[index], scenes[index].Motion.EndViewport, outputAspect);
        }
    }

    private static void ValidateViewportAspect(
        VideoScene scene,
        SceneVisualAsset asset,
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
        var dimensionsMatch = specification.AspectRatio switch
        {
            VideoAspectRatio.Landscape16By9 => output.Width == 1_920 && output.Height == 1_080,
            VideoAspectRatio.Vertical9By16 => output.Width == 1_080 && output.Height == 1_920,
            _ => false,
        };
        if (!dimensionsMatch
            || output.FrameRate != 30
            || !string.Equals(output.VideoCodec, "h264", StringComparison.Ordinal)
            || !string.Equals(output.AudioCodec, "aac", StringComparison.Ordinal)
            || !string.Equals(output.PixelFormat, "yuv420p", StringComparison.Ordinal)
            || output.SampleRateHz != 48_000
            || output.AudioChannels != 2)
        {
            throw new NotSupportedException(
                "The renderer supports only 1920x1080 landscape or 1080x1920 vertical H.264/AAC output profiles.");
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
        SceneVisualAsset asset)
    {
        var width = Math.Max(1, Math.Min(asset.Width, (int)Math.Round(asset.Width * viewport.Width)));
        var height = Math.Max(1, Math.Min(asset.Height, (int)Math.Round(asset.Height * viewport.Height)));
        var x = Math.Clamp((int)Math.Round(asset.Width * viewport.X), 0, asset.Width - width);
        var y = Math.Clamp((int)Math.Round(asset.Height * viewport.Y), 0, asset.Height - height);
        return (x, y, width, height);
    }

    private static string Seconds(int milliseconds) => Number(milliseconds / 1_000m);

    private static string Number(decimal value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private sealed record RenderInputIndexes(
        int? NarrationInput,
        int? MusicInput,
        IReadOnlyList<int> LogoInputs,
        IReadOnlyList<int> NeighborhoodInputs);

    private sealed record NeighborhoodOverlay(
        VideoRenderNeighborhoodAsset Asset,
        int StartMs,
        int DurationMs);

    private readonly record struct PixelBox(int X, int Y, int Width, int Height);

    private sealed record SceneVisualAsset(
        string FilePath,
        int Width,
        int Height,
        bool IsVideo,
        bool IsGeneratedClip,
        int StartMs);
}
