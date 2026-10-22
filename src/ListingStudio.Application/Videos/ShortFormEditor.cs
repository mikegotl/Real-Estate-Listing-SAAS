using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Videos;

namespace ListingStudio.Application.Videos;

/// <summary>
/// A listing photo in walkthrough storyboard order. A photo found in a property video remembers where, so the edit
/// can show that moment of the video moving instead of the still.
/// </summary>
public sealed record ShortFormPhoto(
    Guid MediaId,
    int Width,
    int Height,
    Guid? SourceVideoId = null,
    int? SourceVideoTimestampMs = null);

/// <summary>A steady, detailed stretch of a property video that makes a good shot.</summary>
public sealed record ShortFormVideoClip(Guid PropertyVideoId, int StartMs, int EndMs, double Score);

/// <summary>A finished AI walk-through clip from one storyboard photo to the next.</summary>
public sealed record ShortFormWalkthroughClip(Guid TransitionId, Guid FromMediaId, Guid ToMediaId, int DurationMs);

public sealed record ShortFormEditRequest(
    VideoProductionSpecification Master,
    IReadOnlyList<ShortFormPhoto> StoryboardPhotos,
    IReadOnlyList<CampaignDerivativeVideo> PropertyVideos,
    RequestedDuration Duration,
    int BeatMs,
    CampaignPace Pace,
    ShortFormCopy Copy,
    IReadOnlyList<ShortFormVideoClip>? VideoClips = null,
    IReadOnlyList<ShortFormWalkthroughClip>? WalkthroughClips = null);

/// <summary>
/// Builds the vertical short-form edit (TikTok, Reels, Shorts) instead of trimming the landscape tour: a hook shot
/// with the price and location, then a cut on the music beat every one to two seconds with every shot moving, and an
/// end card with the call to action and the agent's contact. Shots follow the director's tour order, with storyboard
/// photos the tour skipped slotted back in at their storyboard position. Real moving video comes first, like the
/// social examples: a room photo taken from a property video plays that moment of the video, steady stretches of the
/// walkthrough videos and finished AI walk-through clips (between the two photos they join) are shots of their own,
/// and still photos with a Ken Burns move only fill the slots the video cannot.
/// </summary>
public static class ShortFormEditor
{
    public const string EditorVersion = "short-form-editor-v3";

    private static readonly VideoOutputProfile Output = new(1_080, 1_920, 30, "h264", "aac", "yuv420p", 48_000, 2);
    private static readonly NormalizedRect SafeZone = new(0.075m, 0.05m, 0.85m, 0.9m);

    public static VideoProductionSpecification Create(ShortFormEditRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Master);
        ArgumentNullException.ThrowIfNull(request.Copy);
        if (request.BeatMs is < 250 or > 1_500)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The music beat must be between 250 and 1500 ms.");
        }

        var programMs = (int)request.Duration * 1_000;
        var hookMs = Beats(request.BeatMs, 3_000) * request.BeatMs;
        var shotMs = Beats(request.BeatMs, request.Pace == CampaignPace.Fast ? 1_600 : 4_000) * request.BeatMs;
        var minimumEndCardMs = Beats(request.BeatMs, 3_000) * request.BeatMs;
        var middleCount = Math.Max(1, (programMs - hookMs - minimumEndCardMs) / shotMs);
        var crossfadeMs = request.Pace == CampaignPace.Fast ? 0 : Math.Min(400, shotMs / 3);
        var shots = ShotPool(request, shotMs + crossfadeMs);
        if (shots.Count == 0)
        {
            throw new ArgumentException("A short-form edit needs at least one photo or usable video clip.", nameof(request));
        }

        var planned = new List<(Shot Shot, Move Move, int DurationMs)> { (shots[0], Move.HookPush, hookMs) };
        planned.AddRange(Sequence(shots, middleCount).Select(item => (item.Shot, item.Move, shotMs)));
        var endCardMs = programMs - planned.Sum(item => item.DurationMs);
        if (endCardMs < 2 * request.BeatMs)
        {
            throw new ArgumentException("The short-form edit does not fit the requested duration.", nameof(request));
        }

        planned.Add((shots[0], Move.EndPullBack, endCardMs));

        var copy = request.Copy;
        var scenes = new List<VideoScene>(planned.Count);
        var startMs = 0;
        for (var index = 0; index < planned.Count; index++)
        {
            var (shot, move, duration) = planned[index];
            var isHook = index == 0;
            var isEndCard = index == planned.Count - 1;
            var transition = index == 0 || crossfadeMs == 0
                ? new TransitionPlan(TransitionKind.Cut, 0)
                : new TransitionPlan(
                    TransitionKind.Crossfade,
                    Math.Min(crossfadeMs, Math.Min(duration, planned[index - 1].DurationMs) / 3));
            var overlays = new List<TextOverlay>();
            if (isHook)
            {
                overlays.AddRange(HookOverlays(copy.HookLines, duration));
            }

            if (!isHook && !isEndCard && copy.SpecLine is { } spec)
            {
                overlays.Add(new TextOverlay(
                    $"spec-{index + 1}", spec.Text, spec.GroundingKey, 0, duration,
                    OverlayAnchor.TopCenter, new NormalizedRect(0.08m, 0.06m, 0.84m, 0.05m),
                    TextOverlayStyle.SpecLine));
            }

            if (isEndCard)
            {
                var cta = request.Master.CallToAction;
                overlays.Add(new TextOverlay(
                    "closing-cta", cta.Text, cta.GroundingKey, 0, duration,
                    OverlayAnchor.Center, new NormalizedRect(0.08m, 0.30m, 0.84m, 0.12m),
                    TextOverlayStyle.ClosingCta));
            }

            if (copy.AgentContact is { } contact && (!isHook || copy.HookLines.Count == 0))
            {
                overlays.Add(new TextOverlay(
                    $"agent-contact-{index + 1}", contact.Text, contact.GroundingKey, 0, duration,
                    OverlayAnchor.Center,
                    isEndCard
                        ? new NormalizedRect(0.08m, 0.44m, 0.84m, 0.07m)
                        : new NormalizedRect(0.08m, 0.72m, 0.84m, 0.05m),
                    TextOverlayStyle.AgentContact));
            }

            // The clip has to cover the scene and the crossfade into the next one; a photo plays its still instead
            // when its video moment is too short for a long hook or end card.
            var playable = shot.Playable(duration + (index + 1 < planned.Count ? crossfadeMs : 0));
            scenes.Add(new VideoScene(
                index + 1,
                startMs,
                duration,
                playable.Visual(duration + (index + 1 < planned.Count ? crossfadeMs : 0)),
                transition,
                playable.Motion(move, request.Pace),
                overlays,
                [],
                []));
            startMs += duration;
        }

        var narration = copy.KeptNarration?.ToList()
            ?? PlanNarration(copy.VoiceoverLines, scenes[^1].StartMs, programMs);
        if (narration.Any(segment => segment.StartMs < 0 || segment.StartMs + segment.DurationMs > programMs))
        {
            throw new ArgumentException("The kept narration does not fit the short-form duration.", nameof(request));
        }

        scenes = scenes.Select(scene => scene with
        {
            NarrationSegmentIds = narration
                .Where(segment => segment.StartMs < scene.StartMs + scene.DurationMs
                    && segment.StartMs + segment.DurationMs > scene.StartMs)
                .Select(segment => segment.Id)
                .Order(StringComparer.Ordinal)
                .ToArray(),
        }).ToList();
        var music = request.Master.Audio.Music.AssetId is null
            ? request.Master.Audio.Music
            : request.Master.Audio.Music with
            {
                StartMs = 0,
                DurationMs = programMs,
                FadeInMs = Math.Min(request.Master.Audio.Music.FadeInMs, programMs / 4),
                FadeOutMs = Math.Min(request.Master.Audio.Music.FadeOutMs, programMs / 4),
            };
        var addedKeys = copy.AddedFactBindings.Select(binding => binding.Key).ToHashSet(StringComparer.Ordinal);
        return request.Master with
        {
            RequestedDuration = request.Duration,
            AspectRatio = VideoAspectRatio.Vertical9By16,
            Output = Output,
            SafeZone = SafeZone,
            FactBindings = request.Master.FactBindings
                .Where(binding => !addedKeys.Contains(binding.Key))
                .Concat(copy.AddedFactBindings)
                .ToArray(),
            Audio = new AudioPlan(narration, music),
            Scenes = scenes,
        };
    }

    /// <summary>Whole beats closest to the target length, at least one.</summary>
    private static int Beats(int beatMs, int targetMs) => Math.Max(1, (int)Math.Round((double)targetMs / beatMs));

    /// <summary>The title pill first, then the fact pills popping in a quarter second apart.</summary>
    private static IEnumerable<TextOverlay> HookOverlays(IReadOnlyList<GroundedText> lines, int durationMs)
    {
        var y = 0.10m;
        for (var index = 0; index < lines.Count; index++)
        {
            var height = index == 0 ? 0.09m : 0.06m;
            var delay = Math.Min(durationMs / 4, 250 * index);
            yield return new TextOverlay(
                $"hook-{index + 1}",
                lines[index].Text,
                lines[index].GroundingKey,
                delay,
                durationMs - delay,
                OverlayAnchor.Center,
                new NormalizedRect(0.08m, y, 0.84m, height),
                index == 0 ? TextOverlayStyle.HookTitle : TextOverlayStyle.HookFact);
            y += height + 0.015m;
        }
    }

    /// <summary>
    /// The opening line starts just after the cut in; the call to action (the last line) starts with the end card,
    /// or earlier when it would otherwise run past the end; the lines between follow one another with a short pause.
    /// The renderer moves a line later when the one before it runs long.
    /// </summary>
    private static List<NarrationSegment> PlanNarration(
        IReadOnlyList<GroundedText> lines,
        int endCardStartMs,
        int programMs)
    {
        var result = new List<NarrationSegment>(lines.Count);
        if (lines.Count == 0)
        {
            return result;
        }

        var starts = new int[lines.Count];
        starts[0] = 300;
        for (var index = 1; index < lines.Count; index++)
        {
            starts[index] = starts[index - 1] + EstimateMs(lines[index - 1].Text) + 250;
        }

        if (lines.Count > 1)
        {
            var latestCtaStart = programMs - EstimateMs(lines[^1].Text) - 300;
            starts[^1] = Math.Max(starts[^1], Math.Min(endCardStartMs, latestCtaStart));
        }

        for (var index = 0; index < lines.Count; index++)
        {
            var start = Math.Min(starts[index], programMs - 1);
            var end = index + 1 < lines.Count ? Math.Min(starts[index + 1], programMs) : programMs;
            result.Add(new NarrationSegment(
                $"short-{lines[index].GroundingKey}",
                start,
                Math.Max(1, end - start),
                lines[index].Text,
                lines[index].GroundingKey));
        }

        return result;
    }

    /// <summary>About 2.6 words a second for an upbeat social read.</summary>
    public static int EstimateMs(string text) =>
        (int)(text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length * 10_000L / 26);

    /// <summary>
    /// Photos and walkthrough-video shots in the order the tour shows them; storyboard photos the tour left out go
    /// back in at their storyboard position. A photo taken from a property video becomes that moment of the video.
    /// Steady stretches of a video join next to the photos taken from it (in video time), or, for a video with no
    /// photos in the storyboard, where the tour used that video, or after the photos. A video window is used only
    /// when it holds a whole shot.
    /// </summary>
    private static List<Shot> ShotPool(ShortFormEditRequest request, int videoShotMs)
    {
        var photos = request.StoryboardPhotos
            .Where(photo => photo.Width > 0 && photo.Height > 0)
            .Select((photo, index) => (Photo: photo, Index: index))
            .GroupBy(item => item.Photo.MediaId)
            .ToDictionary(group => group.Key, group => group.First());
        var videos = request.PropertyVideos
            .Where(video => video.Width > 0 && video.Height > 0 && video.DurationMs >= videoShotMs)
            .GroupBy(video => video.PropertyVideoId)
            .ToDictionary(group => group.Key, group => group.First());
        var clips = (request.VideoClips ?? [])
            .Where(clip => videos.ContainsKey(clip.PropertyVideoId) && clip.EndMs > clip.StartMs)
            .GroupBy(clip => clip.PropertyVideoId)
            .ToDictionary(group => group.Key, group => group.OrderBy(clip => clip.StartMs).ToList());

        Shot PhotoShot(ShortFormPhoto photo) =>
            photo.SourceVideoId is { } sourceId
            && photo.SourceVideoTimestampMs is { } timestamp
            && videos.TryGetValue(sourceId, out var source)
                ? new Shot(photo, source, timestamp)
                : new Shot(photo, null, 0);

        var ordered = new List<(decimal Order, Shot Shot)>();
        var used = new HashSet<Guid>();
        var videoOrder = new Dictionary<Guid, decimal>();
        var lastOrder = -1m;
        foreach (var scene in request.Master.Scenes)
        {
            var visual = scene.VisualSource;
            if (visual.Kind == VisualSourceKind.PropertyVideo)
            {
                if (visual.PropertyVideoId is { } videoId && videos.TryGetValue(videoId, out var video))
                {
                    lastOrder += 0.001m;
                    videoOrder.TryAdd(videoId, lastOrder);
                    if (!clips.ContainsKey(videoId))
                    {
                        var start = Math.Clamp(visual.PropertyVideoStartMs ?? 0, 0, video.DurationMs - videoShotMs);
                        ordered.Add((lastOrder, new Shot(null, video, start + (videoShotMs / 2))));
                    }
                }

                continue;
            }

            var mediaId = visual.Kind == VisualSourceKind.PropertyMedia
                ? visual.PropertyMediaId
                : visual.FallbackPropertyMediaId;
            if (mediaId is { } id && photos.TryGetValue(id, out var photo) && used.Add(id))
            {
                lastOrder = Math.Max(lastOrder + 0.001m, photo.Index);
                ordered.Add((lastOrder, PhotoShot(photo.Photo)));
            }
        }

        foreach (var (photo, index) in photos.Values.Where(item => !used.Contains(item.Photo.MediaId)))
        {
            ordered.Add((index, PhotoShot(photo)));
        }

        ordered.AddRange(ClipShots(clips, videos, ordered, videoOrder, photos.Count));
        ordered.AddRange(WalkthroughShots(request.WalkthroughClips, ordered));
        var result = ordered.OrderBy(item => item.Order).Select(item => item.Shot).ToList();

        // The hook and the end card open on the first storyboard photo (usually the front of the home, moving when it
        // came from a video) so the pills sit on the house; with no photos at all the first clip opens.
        var firstPhoto = result.FindIndex(shot => shot.Photo is not null && shot.Walkthrough is null);
        if (firstPhoto > 0)
        {
            var hook = result[firstPhoto];
            result.RemoveAt(firstPhoto);
            result.Insert(0, hook);
        }

        return result;
    }

    /// <summary>
    /// Clips placed in the tour order. Clips within a few seconds of a storyboard photo from the same video are left
    /// out, since that photo already plays that moment.
    /// </summary>
    private static IEnumerable<(decimal Order, Shot Shot)> ClipShots(
        Dictionary<Guid, List<ShortFormVideoClip>> clips,
        Dictionary<Guid, CampaignDerivativeVideo> videos,
        List<(decimal Order, Shot Shot)> photoShots,
        Dictionary<Guid, decimal> videoOrder,
        int photoCount)
    {
        const int SameMomentMs = 3_000;
        var nextUnplaced = (decimal)photoCount;
        var uploadOrder = videos.Keys.Select((id, index) => (id, index)).ToDictionary(item => item.id, item => item.index);
        foreach (var (videoId, videoClips) in clips
            .OrderBy(item => videoOrder.GetValueOrDefault(item.Key, decimal.MaxValue))
            .ThenBy(item => uploadOrder[item.Key]))
        {
            var video = videos[videoId];
            var anchors = photoShots
                .Where(item => item.Shot.Video?.PropertyVideoId == videoId)
                .Select(item => (item.Order, item.Shot.CenterMs))
                .OrderBy(anchor => anchor.CenterMs)
                .ToArray();
            var blockStart = videoOrder.TryGetValue(videoId, out var tourOrder) ? tourOrder : nextUnplaced;
            if (!videoOrder.ContainsKey(videoId) && anchors.Length == 0)
            {
                nextUnplaced += 1;
            }

            for (var index = 0; index < videoClips.Count; index++)
            {
                var clip = videoClips[index];
                var center = (clip.StartMs + clip.EndMs) / 2;
                if (anchors.Any(anchor => Math.Abs(anchor.CenterMs - center) < SameMomentMs))
                {
                    continue;
                }

                // Between the photos taken just before and just after it in the video, or in video order as a block.
                // Under half a thousandth, so clips never jump past the next photo in the tour.
                var fraction = (decimal)center / Math.Max(1, video.DurationMs) * 0.0005m;
                decimal order;
                if (anchors.Length == 0)
                {
                    order = blockStart + fraction * 1_000;
                }
                else
                {
                    var before = anchors.LastOrDefault(anchor => anchor.CenterMs <= center);
                    order = anchors.Any(anchor => anchor.CenterMs <= center)
                        ? before.Order + fraction
                        : anchors[0].Order - 0.0005m + fraction;
                }

                yield return (order, new Shot(null, video, center));
            }
        }
    }

    /// <summary>
    /// Each finished AI walk-through clip right after the photo it walks from, when both of its photos are in the
    /// edit; the renderer plays the walk itself, sped up to fit the shot.
    /// </summary>
    private static IEnumerable<(decimal Order, Shot Shot)> WalkthroughShots(
        IReadOnlyList<ShortFormWalkthroughClip>? walkthroughs,
        List<(decimal Order, Shot Shot)> photoShots)
    {
        var photoOrders = photoShots
            .Where(item => item.Shot.Photo is not null)
            .GroupBy(item => item.Shot.Photo!.MediaId)
            .ToDictionary(group => group.Key, group => (group.First().Order, group.First().Shot.Photo!));
        foreach (var walkthrough in (walkthroughs ?? [])
            .Where(clip => clip.DurationMs > 0)
            .GroupBy(clip => (clip.FromMediaId, clip.ToMediaId))
            .Select(group => group.First()))
        {
            if (photoOrders.TryGetValue(walkthrough.FromMediaId, out var from)
                && photoOrders.ContainsKey(walkthrough.ToMediaId))
            {
                // After the photo and before anything else the tour places after it.
                yield return (from.Order + 0.0007m, new Shot(from.Item2, null, 0, walkthrough));
            }
        }
    }

    /// <summary>
    /// The middle shots: an even spread through the tour when there are more shots than slots (the hook shot opens
    /// and closes the video, so it is left out), or every shot again with a different move when there are fewer.
    /// Video shots win the slots: stills are used only for the slots the video shots cannot fill.
    /// </summary>
    private static List<(Shot Shot, Move Move)> Sequence(List<Shot> shots, int count)
    {
        var pool = shots.Count > 1 ? shots.Skip(1).ToList() : shots;
        var moving = pool.Where(shot => shot.IsMoving).ToList();
        if (moving.Count > 0 && moving.Count < pool.Count)
        {
            if (moving.Count >= count)
            {
                pool = moving;
            }
            else
            {
                var stills = pool.Where(shot => !shot.IsMoving).ToList();
                var wanted = count - moving.Count;
                var keep = Enumerable.Range(0, Math.Min(wanted, stills.Count))
                    .Select(index => stills[(int)((long)index * stills.Count / Math.Min(wanted, stills.Count))])
                    .ToHashSet();
                pool = pool.Where(shot => shot.IsMoving || keep.Contains(shot)).ToList();
            }
        }

        var moves = new[] { Move.PushIn, Move.PullOut, Move.Pan };
        var result = new List<(Shot, Move)>(count);
        for (var index = 0; index < count; index++)
        {
            var spread = pool.Count >= count;
            var shot = spread ? pool[(int)((long)index * pool.Count / count)] : pool[index % pool.Count];
            var pass = spread ? 0 : index / pool.Count;
            result.Add((shot, moves[(index + pass) % moves.Length]));
        }

        return result;
    }

    private enum Move
    {
        HookPush,
        PushIn,
        PullOut,
        Pan,
        EndPullBack,
    }

    /// <summary>
    /// A still photo, a moment of a property video centred on <paramref name="CenterMs"/>, a storyboard photo taken
    /// from a video (both set), which plays as video when the video has room for the shot, or an AI walk-through
    /// clip leaving <paramref name="Photo"/> (which it falls back to).
    /// </summary>
    private sealed record Shot(
        ShortFormPhoto? Photo,
        CampaignDerivativeVideo? Video,
        int CenterMs,
        ShortFormWalkthroughClip? Walkthrough = null)
    {
        public bool IsMoving => Video is not null || Walkthrough is not null;

        /// <summary>This shot as a still when its video is too short to play <paramref name="durationMs"/>.</summary>
        public Shot Playable(int durationMs) =>
            Video is not null && Photo is not null && Video.DurationMs < durationMs ? this with { Video = null }
            : Walkthrough is not null && Walkthrough.DurationMs < durationMs ? this with { Walkthrough = null }
            : this;

        public VisualSource Visual(int durationMs) => Walkthrough is not null
            ? new VisualSource(VisualSourceKind.GeneratedClip, null, Walkthrough.TransitionId, Photo!.MediaId, null)
            : Video is null
            ? new VisualSource(VisualSourceKind.PropertyMedia, Photo!.MediaId, null, null, null)
            : new VisualSource(
                VisualSourceKind.PropertyVideo,
                null,
                null,
                null,
                null,
                Video.PropertyVideoId,
                Math.Clamp(CenterMs - (durationMs / 2), 0, Math.Max(0, Video.DurationMs - durationMs)));

        /// <summary>
        /// Every photo moves: a fast shot covers about a fifth of the frame in under two seconds, several times the
        /// speed of the tour's slow drift. Viewports keep the 9:16 shape so nothing stretches.
        /// </summary>
        public MotionPlan Motion(Move move, CampaignPace pace)
        {
            var fill = Fill();
            if (IsMoving)
            {
                return new MotionPlan(MotionKind.None, fill, fill, MotionEasing.Linear);
            }

            var zoom = pace == CampaignPace.Fast ? 0.80m : 0.86m;
            return move switch
            {
                Move.HookPush => new MotionPlan(MotionKind.KenBurns, fill, Scale(fill, 0.88m, 0.5m, 0.4m), MotionEasing.EaseInOut),
                Move.PushIn => new MotionPlan(MotionKind.KenBurns, fill, Scale(fill, zoom, 0.5m, 0.45m), MotionEasing.EaseInOut),
                Move.PullOut => new MotionPlan(MotionKind.KenBurns, Scale(fill, zoom, 0.5m, 0.5m), fill, MotionEasing.EaseInOut),
                Move.Pan => Pan(fill, zoom),
                _ => new MotionPlan(MotionKind.KenBurns, Scale(fill, 0.86m, 0.5m, 0.5m), fill, MotionEasing.EaseInOut),
            };
        }

        /// <summary>The largest 9:16 window of the source, centred.</summary>
        private NormalizedRect Fill()
        {
            var (width, height) = Video is not null ? (Video.Width, Video.Height) : (Photo!.Width, Photo.Height);
            var normalizedWidth = (decimal)Output.Width / Output.Height * height / width;
            return normalizedWidth <= 1
                ? new NormalizedRect((1 - normalizedWidth) / 2, 0, normalizedWidth, 1)
                : new NormalizedRect(0, (1 - 1 / normalizedWidth) / 2, 1, 1 / normalizedWidth);
        }

        /// <summary>A sideways glide across a wide photo, or a push toward the upper third of a tall one.</summary>
        private static MotionPlan Pan(NormalizedRect fill, decimal zoom)
        {
            var window = Scale(fill, zoom, 0.5m, 0.5m);
            var travel = Math.Min(window.X, 0.08m);
            if (travel < 0.03m)
            {
                return new MotionPlan(MotionKind.KenBurns, fill, Scale(fill, zoom, 0.5m, 0.3m), MotionEasing.EaseInOut);
            }

            return new MotionPlan(
                MotionKind.KenBurns,
                window with { X = window.X - travel },
                window with { X = window.X + travel },
                MotionEasing.EaseInOut);
        }

        private static NormalizedRect Scale(NormalizedRect fill, decimal factor, decimal focusX, decimal focusY)
        {
            var width = fill.Width * factor;
            var height = fill.Height * factor;
            var x = Math.Clamp(fill.X + (fill.Width - width) * focusX, 0, 1 - width);
            var y = Math.Clamp(fill.Y + (fill.Height - height) * focusY, 0, 1 - height);
            return new NormalizedRect(x, y, width, height);
        }
    }
}
