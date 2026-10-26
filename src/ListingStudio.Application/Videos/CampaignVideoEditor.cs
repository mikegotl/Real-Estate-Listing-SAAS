using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Videos;

namespace ListingStudio.Application.Videos;

public enum CampaignVideoEditKind
{
    SwapShot,
    MoveShot,
    RemoveShot,
    RemoveText,
    ChangeText,
    ChangeMusic,
    ChangeMusicVolume,
    RemoveVoiceover,
    ChangeCaptionStyle,
    RemoveCaptions,
    ChangeVoice,
}

/// <summary>
/// One change to a finished campaign video, chosen by the video chat from a fixed menu. Shot numbers are the
/// numbers of the video the change was proposed against, starting at 1.
/// </summary>
public sealed record CampaignVideoEdit(
    CampaignVideoEditKind Kind,
    int? Shot = null,
    Guid? PhotoId = null,
    int? Position = null,
    string? TextId = null,
    string? FactKey = null,
    CampaignSoundtrack? Music = null,
    int? VolumeChangeDb = null,
    CampaignCaptionStyle? CaptionStyle = null,
    CampaignVoiceAccent? VoiceAccent = null);

/// <summary>A listing photo the chat may put into the video, with the room it shows.</summary>
public sealed record CampaignVideoPhoto(Guid Id, int Width, int Height, string? Room, string? Description);

public sealed record CampaignVideoEditContext(
    IReadOnlyList<CampaignVideoPhoto> Photos,
    IReadOnlyDictionary<Guid, int> PropertyVideoDurationsMs);

public sealed record CampaignVideoEditOutcome(CampaignVideoEdit Edit, string Description);

public sealed record CampaignVideoEditResult(
    VideoProductionSpecification Specification,
    IReadOnlyList<CampaignVideoEditOutcome> Applied,
    IReadOnlyList<CampaignVideoEditOutcome> Rejected,
    CampaignVoiceAccent? NewVoice);

/// <summary>
/// Applies chat edits to a finished video's plan without planning it again. Every shot keeps its place on the
/// timeline, so the voiceover, captions and music beat stay where they were: swapping or moving a shot only changes
/// what is on screen in that slot, and removing a shot lets its neighbour hold for longer. Text can only be removed
/// or replaced by another verified fact, and photos only come from the listing, so a revision never invents a fact
/// or an image.
/// </summary>
public static class CampaignVideoEditor
{
    public const int MaximumEdits = 12;
    public const string CaptionOverlayPrefix = "caption-";

    public static CampaignVideoEditResult Apply(
        VideoProductionSpecification source,
        IReadOnlyList<CampaignVideoEdit> edits,
        CampaignVideoEditContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(edits);
        ArgumentNullException.ThrowIfNull(context);
        var applied = new List<CampaignVideoEditOutcome>();
        var rejected = new List<CampaignVideoEditOutcome>();
        var photos = context.Photos.ToDictionary(photo => photo.Id);
        var originalDurations = source.Scenes.ToDictionary(scene => scene.SceneNumber, scene => scene.DurationMs);
        var slots = source.Scenes
            .OrderBy(scene => scene.SceneNumber)
            .Select(scene => new Slot(scene.SceneNumber, scene, new Shot(scene.SceneNumber, scene.VisualSource, scene.Motion)))
            .ToList();
        var specification = source;
        CampaignVoiceAccent? voice = null;

        void Reject(CampaignVideoEdit edit, string reason) => rejected.Add(new CampaignVideoEditOutcome(edit, reason));
        void Accept(CampaignVideoEdit edit, string description) => applied.Add(new CampaignVideoEditOutcome(edit, description));
        Slot? FindSlot(int? shot) => shot is null ? null : slots.FirstOrDefault(slot => slot.OriginalNumber == shot);

        foreach (var edit in edits.Take(MaximumEdits).Where(edit => edit.Kind == CampaignVideoEditKind.SwapShot))
        {
            var slot = FindSlot(edit.Shot);
            if (slot is null)
            {
                Reject(edit, "That shot is not in this video.");
                continue;
            }

            if (edit.PhotoId is not { } photoId || !photos.TryGetValue(photoId, out var photo))
            {
                Reject(edit, "Only this listing's own photos can go into the video.");
                continue;
            }

            if (photo.Width <= 0 || photo.Height <= 0)
            {
                Reject(edit, "That photo has no usable size.");
                continue;
            }

            slot.Shot = new Shot(
                slot.Shot.OriginalNumber,
                new VisualSource(VisualSourceKind.PropertyMedia, photo.Id, null, null, null),
                PushIn(photo, source.Output));
            Accept(edit, $"Show {Name(photo)} in shot {edit.Shot}.");
        }

        foreach (var edit in edits.Take(MaximumEdits).Where(edit => edit.Kind == CampaignVideoEditKind.MoveShot))
        {
            var from = slots.FindIndex(slot => slot.Shot.OriginalNumber == edit.Shot);
            if (from < 0 || edit.Position is not { } position || position < 1 || position > slots.Count)
            {
                Reject(edit, "That shot or position is not in this video.");
                continue;
            }

            var shots = slots.Select(slot => slot.Shot).ToList();
            var moving = shots[from];
            shots.RemoveAt(from);
            shots.Insert(position - 1, moving);
            var problem = Enumerable.Range(0, slots.Count)
                .Select(index => FitProblem(shots[index], slots[index].Scene.DurationMs, originalDurations, context))
                .FirstOrDefault(reason => reason is not null);
            if (problem is not null)
            {
                Reject(edit, problem);
                continue;
            }

            for (var index = 0; index < slots.Count; index++)
            {
                slots[index].Shot = shots[index];
            }

            Accept(edit, $"Move shot {edit.Shot} to position {position}.");
        }

        foreach (var edit in edits.Take(MaximumEdits)
                     .Where(edit => edit.Kind == CampaignVideoEditKind.RemoveShot)
                     .OrderByDescending(edit => edit.Shot))
        {
            var index = slots.FindIndex(slot => slot.Shot.OriginalNumber == edit.Shot);
            if (index < 0)
            {
                Reject(edit, "That shot is not in this video.");
                continue;
            }

            if (slots.Count == 1)
            {
                Reject(edit, "A video needs at least one shot.");
                continue;
            }

            var removed = slots[index];
            var keeperIndex = index > 0 ? index - 1 : 1;
            var keeper = slots[keeperIndex];
            var merged = keeper.Scene.DurationMs + removed.Scene.DurationMs;
            var problem = FitProblem(keeper.Shot, merged, originalDurations, context);
            if (problem is not null && index > 0 && index + 1 < slots.Count)
            {
                keeperIndex = index + 1;
                keeper = slots[keeperIndex];
                problem = FitProblem(keeper.Shot, keeper.Scene.DurationMs + removed.Scene.DurationMs, originalDurations, context);
            }

            if (problem is not null)
            {
                Reject(edit, "The shots next to it are too short to fill its time. Try swapping it for a photo instead.");
                continue;
            }

            keeper.Scene = keeperIndex < index
                ? Absorb(keeper.Scene, removed.Scene, after: true)
                : Absorb(keeper.Scene, removed.Scene, after: false) with { TransitionIn = removed.Scene.TransitionIn };
            slots.RemoveAt(index);
            Accept(edit, $"Remove shot {edit.Shot}; the shot next to it holds for longer.");
        }

        var startMs = 0;
        var scenes = new List<VideoScene>();
        for (var index = 0; index < slots.Count; index++)
        {
            var slot = slots[index];
            var visual = Fit(slot.Shot.Visual, slot.Scene.DurationMs, context);
            scenes.Add(slot.Scene with
            {
                SceneNumber = index + 1,
                StartMs = startMs,
                VisualSource = visual,
                Motion = slot.Shot.Motion,
                TransitionIn = index == 0 ? new TransitionPlan(TransitionKind.Cut, 0) : slot.Scene.TransitionIn,
            });
            startMs += slot.Scene.DurationMs;
        }

        specification = specification with { Scenes = scenes };
        foreach (var edit in edits.Take(MaximumEdits))
        {
            switch (edit.Kind)
            {
                case CampaignVideoEditKind.RemoveText:
                    if (string.IsNullOrWhiteSpace(edit.TextId)
                        || edit.TextId.StartsWith(CaptionOverlayPrefix, StringComparison.Ordinal)
                        || !specification.Scenes.Any(scene => scene.TextOverlays.Any(overlay => overlay.Id == edit.TextId)))
                    {
                        Reject(edit, "That on-screen text is not in this video.");
                        break;
                    }

                    var removedText = specification.Scenes.SelectMany(scene => scene.TextOverlays).First(overlay => overlay.Id == edit.TextId).Text;
                    specification = MapOverlays(specification, overlays => overlays.Where(overlay => overlay.Id != edit.TextId));
                    Accept(edit, $"Remove the text \"{removedText}\".");
                    break;
                case CampaignVideoEditKind.ChangeText:
                    var target = specification.Scenes.SelectMany(scene => scene.TextOverlays)
                        .FirstOrDefault(overlay => overlay.Id == edit.TextId
                            && !overlay.Id.StartsWith(CaptionOverlayPrefix, StringComparison.Ordinal));
                    var binding = specification.FactBindings.FirstOrDefault(candidate => candidate.Key == edit.FactKey);
                    if (target is null)
                    {
                        Reject(edit, "That on-screen text is not in this video.");
                        break;
                    }

                    if (binding is null || string.IsNullOrWhiteSpace(binding.Value) || binding.Value.Length > 120)
                    {
                        Reject(edit, "On-screen text can only show the listing's verified facts. Update the property details to change a fact.");
                        break;
                    }

                    var text = target.Text.Any(char.IsLetter) && !target.Text.Any(char.IsLower)
                        ? binding.Value.ToUpperInvariant()
                        : binding.Value;
                    specification = MapOverlays(specification, overlays => overlays.Select(overlay => overlay.Id == target.Id
                        ? overlay with { Text = text, GroundingKey = binding.Key }
                        : overlay));
                    Accept(edit, $"Change \"{target.Text}\" to \"{text}\".");
                    break;
                case CampaignVideoEditKind.ChangeMusic:
                    if (edit.Music is not { } music
                        || music is CampaignSoundtrack.AutoByHomeEra or CampaignSoundtrack.AddInApp
                        || !Enum.IsDefined(music))
                    {
                        Reject(edit, "Choose one of the built-in tracks, or no music.");
                        break;
                    }

                    specification = specification with
                    {
                        Audio = specification.Audio with
                        {
                            Music = CampaignCreativeDirector.MusicFor(music, specification),
                        },
                    };
                    Accept(edit, music == CampaignSoundtrack.None ? "Turn the music off." : $"Switch the music to {TrackName(music)}.");
                    break;
                case CampaignVideoEditKind.ChangeMusicVolume:
                    var current = specification.Audio.Music;
                    if (current.AssetId is null)
                    {
                        Reject(edit, "This video has no music to make louder or quieter.");
                        break;
                    }

                    if (edit.VolumeChangeDb is not { } change || change == 0 || Math.Abs(change) > 12)
                    {
                        Reject(edit, "Music volume can change by up to 12 dB at a time.");
                        break;
                    }

                    var gain = Math.Clamp(current.GainDb + change, -40, 0);
                    if (gain == current.GainDb)
                    {
                        Reject(edit, change > 0 ? "The music is already as loud as it can go." : "The music is already as quiet as it can go.");
                        break;
                    }

                    specification = specification with
                    {
                        Audio = specification.Audio with { Music = current with { GainDb = gain } },
                    };
                    Accept(edit, change > 0 ? "Make the music louder." : "Make the music quieter.");
                    break;
                case CampaignVideoEditKind.RemoveVoiceover:
                    if (specification.Audio.NarrationSegments.Count == 0)
                    {
                        Reject(edit, "This video already has no voiceover.");
                        break;
                    }

                    specification = WithoutVoiceover(specification);
                    Accept(edit, "Remove the voiceover and its captions.");
                    break;
                case CampaignVideoEditKind.RemoveCaptions:
                    if (!HasCaptions(specification))
                    {
                        Reject(edit, "This video has no captions.");
                        break;
                    }

                    specification = MapOverlays(specification, overlays => overlays.Where(overlay => !IsCaption(overlay)));
                    Accept(edit, "Remove the captions.");
                    break;
                case CampaignVideoEditKind.ChangeCaptionStyle:
                    if (!HasCaptions(specification) || edit.CaptionStyle is not { } style || !Enum.IsDefined(style))
                    {
                        Reject(edit, "This video has no captions to restyle.");
                        break;
                    }

                    var token = style == CampaignCaptionStyle.Pop ? TextOverlayStyle.PopCaption : TextOverlayStyle.LowerThird;
                    specification = MapOverlays(specification, overlays => overlays.Select(overlay => IsCaption(overlay)
                        ? overlay with { StyleToken = token }
                        : overlay));
                    Accept(edit, style == CampaignCaptionStyle.Pop ? "Use bold pop-up captions." : "Use classic captions.");
                    break;
                case CampaignVideoEditKind.ChangeVoice:
                    if (specification.Audio.NarrationSegments.Count == 0)
                    {
                        Reject(edit, "This video has no voiceover to re-voice.");
                        break;
                    }

                    if (edit.VoiceAccent is not { } accent || !Enum.IsDefined(accent))
                    {
                        Reject(edit, "Choose one of the supported voices.");
                        break;
                    }

                    voice = accent;
                    Accept(edit, $"Re-record the voiceover with the {Words(accent.ToString())} voice.");
                    break;
            }
        }

        if (voice is not null && specification.Audio.NarrationSegments.Count == 0)
        {
            voice = null;
        }

        return new CampaignVideoEditResult(specification, applied, rejected, voice);
    }

    /// <summary>What the chat sees of a video: its shots in order with what is on screen and heard in each.</summary>
    public static IReadOnlyList<CampaignVideoShotSummary> Describe(
        VideoProductionSpecification specification,
        IReadOnlyList<CampaignVideoPhoto> photos)
    {
        var byId = photos.ToDictionary(photo => photo.Id);
        var segments = specification.Audio.NarrationSegments.ToDictionary(segment => segment.Id, StringComparer.Ordinal);
        return specification.Scenes.OrderBy(scene => scene.SceneNumber).Select(scene =>
        {
            var visual = scene.VisualSource;
            var photoId = visual.PropertyMediaId ?? visual.FallbackPropertyMediaId;
            var photo = photoId is { } id && byId.TryGetValue(id, out var found) ? found : null;
            var shows = visual.Kind switch
            {
                VisualSourceKind.PropertyVideo => "a moment of the agent's walkthrough video",
                VisualSourceKind.GeneratedClip => photo is null
                    ? "an AI walk-through clip"
                    : $"an AI walk-through clip starting from {Name(photo)}",
                _ => photo is null ? "a listing photo" : Name(photo),
            };
            return new CampaignVideoShotSummary(
                scene.SceneNumber,
                scene.StartMs,
                scene.DurationMs,
                shows,
                visual.Kind == VisualSourceKind.PropertyMedia ? photoId : null,
                scene.TextOverlays
                    .Where(overlay => !IsCaption(overlay))
                    .Select(overlay => new CampaignVideoTextSummary(overlay.Id, overlay.Text, overlay.StyleToken.ToString()))
                    .ToArray(),
                string.Join(' ', scene.NarrationSegmentIds
                    .Where(segments.ContainsKey)
                    .Select(segmentId => segments[segmentId].Text)));
        }).ToArray();
    }

    public static bool HasCaptions(VideoProductionSpecification specification) =>
        specification.Scenes.Any(scene => scene.TextOverlays.Any(IsCaption));

    public static bool IsCaption(TextOverlay overlay) =>
        overlay.Id.StartsWith(CaptionOverlayPrefix, StringComparison.Ordinal)
        && overlay.StyleToken is TextOverlayStyle.PopCaption or TextOverlayStyle.LowerThird;

    public static CampaignCaptionStyle? CaptionStyleOf(VideoProductionSpecification specification) =>
        specification.Scenes.SelectMany(scene => scene.TextOverlays).FirstOrDefault(IsCaption) is { } caption
            ? caption.StyleToken == TextOverlayStyle.PopCaption ? CampaignCaptionStyle.Pop : CampaignCaptionStyle.Classic
            : null;

    public static string TrackName(CampaignSoundtrack soundtrack) => soundtrack switch
    {
        CampaignSoundtrack.HeritageWarm => "Heritage warm",
        CampaignSoundtrack.ModernAmbient => "Modern ambient",
        CampaignSoundtrack.UpbeatLifestyle => "Upbeat lifestyle",
        CampaignSoundtrack.None => "no music",
        _ => Words(soundtrack.ToString()),
    };

    public static CampaignSoundtrack? TrackOf(MusicPlan music) => music.AssetId switch
    {
        null => CampaignSoundtrack.None,
        CampaignCreativeDirector.HeritageWarmAssetId => CampaignSoundtrack.HeritageWarm,
        CampaignCreativeDirector.ModernAmbientAssetId => CampaignSoundtrack.ModernAmbient,
        CampaignCreativeDirector.UpbeatLifestyleAssetId => CampaignSoundtrack.UpbeatLifestyle,
        _ => null,
    };

    private static VideoProductionSpecification WithoutVoiceover(VideoProductionSpecification specification)
    {
        var withoutCaptions = MapOverlays(specification, overlays => overlays.Where(overlay => !IsCaption(overlay)));
        var music = withoutCaptions.Audio.Music;
        return withoutCaptions with
        {
            Audio = new AudioPlan(
                [],
                // Music carries a video with no voiceover, so it plays at full level, as a new silent cut would.
                music.AssetId is null ? music : music with { GainDb = 0 }),
            Scenes = withoutCaptions.Scenes.Select(scene => scene with { NarrationSegmentIds = [] }).ToArray(),
        };
    }

    private static VideoProductionSpecification MapOverlays(
        VideoProductionSpecification specification,
        Func<IEnumerable<TextOverlay>, IEnumerable<TextOverlay>> map) => specification with
        {
            Scenes = specification.Scenes.Select(scene => scene with { TextOverlays = map(scene.TextOverlays).ToArray() }).ToArray(),
        };

    /// <summary>The kept scene takes over the removed scene's time, text, logos and voiceover lines.</summary>
    private static VideoScene Absorb(VideoScene keeper, VideoScene removed, bool after)
    {
        var shiftKeeper = after ? 0 : removed.DurationMs;
        var shiftRemoved = after ? keeper.DurationMs : 0;
        return keeper with
        {
            DurationMs = keeper.DurationMs + removed.DurationMs,
            TextOverlays = keeper.TextOverlays
                .Select(overlay => overlay with { StartOffsetMs = overlay.StartOffsetMs + shiftKeeper })
                .Concat(removed.TextOverlays.Select(overlay => overlay with { StartOffsetMs = overlay.StartOffsetMs + shiftRemoved }))
                .ToArray(),
            LogoOverlays = keeper.LogoOverlays
                .Select(overlay => overlay with { StartOffsetMs = overlay.StartOffsetMs + shiftKeeper })
                .Concat(removed.LogoOverlays.Select(overlay => overlay with { StartOffsetMs = overlay.StartOffsetMs + shiftRemoved }))
                .ToArray(),
            NarrationSegmentIds = after
                ? keeper.NarrationSegmentIds.Concat(removed.NarrationSegmentIds).ToArray()
                : removed.NarrationSegmentIds.Concat(keeper.NarrationSegmentIds).ToArray(),
        };
    }

    private static string? FitProblem(
        Shot shot,
        int durationMs,
        Dictionary<int, int> originalDurations,
        CampaignVideoEditContext context)
    {
        switch (shot.Visual.Kind)
        {
            case VisualSourceKind.GeneratedClip:
                // A walk-through clip was cut to its own slot; it cannot stretch to hold a longer one.
                return durationMs > originalDurations[shot.OriginalNumber]
                    ? "An AI walk-through clip cannot fill a longer slot."
                    : null;
            case VisualSourceKind.PropertyVideo:
                return shot.Visual.PropertyVideoId is { } videoId
                    && context.PropertyVideoDurationsMs.TryGetValue(videoId, out var videoMs)
                    && videoMs >= durationMs
                        ? null
                        : "The walkthrough video moment is too short for that slot.";
            default:
                return null;
        }
    }

    private static VisualSource Fit(VisualSource visual, int durationMs, CampaignVideoEditContext context)
    {
        if (visual.Kind != VisualSourceKind.PropertyVideo
            || visual.PropertyVideoId is not { } videoId
            || !context.PropertyVideoDurationsMs.TryGetValue(videoId, out var videoMs))
        {
            return visual;
        }

        var start = Math.Clamp(visual.PropertyVideoStartMs ?? 0, 0, Math.Max(0, videoMs - durationMs));
        return visual with { PropertyVideoStartMs = start };
    }

    /// <summary>A slow push into the largest centred window of the photo that has the video's shape.</summary>
    private static MotionPlan PushIn(CampaignVideoPhoto photo, VideoOutputProfile output)
    {
        var normalizedWidth = (decimal)output.Width / output.Height * photo.Height / photo.Width;
        var fill = normalizedWidth <= 1
            ? new NormalizedRect((1 - normalizedWidth) / 2, 0, normalizedWidth, 1)
            : new NormalizedRect(0, (1 - 1 / normalizedWidth) / 2, 1, 1 / normalizedWidth);
        var width = fill.Width * 0.88m;
        var height = fill.Height * 0.88m;
        var end = new NormalizedRect(
            Math.Clamp(fill.X + (fill.Width - width) * 0.5m, 0, 1 - width),
            Math.Clamp(fill.Y + (fill.Height - height) * 0.45m, 0, 1 - height),
            width,
            height);
        return new MotionPlan(MotionKind.KenBurns, fill, end, MotionEasing.EaseInOut);
    }

    private static string Name(CampaignVideoPhoto photo) =>
        string.IsNullOrWhiteSpace(photo.Room) ? "another listing photo" : $"the {photo.Room.Trim().ToLowerInvariant()} photo";

    private static string Words(string value) => string.Concat(value.Select((character, index) =>
        index > 0 && char.IsUpper(character) ? $" {character}" : character.ToString()));

    private sealed record Shot(int OriginalNumber, VisualSource Visual, MotionPlan Motion);

    private sealed class Slot(int originalNumber, VideoScene scene, Shot shot)
    {
        public int OriginalNumber { get; } = originalNumber;

        public VideoScene Scene { get; set; } = scene;

        public Shot Shot { get; set; } = shot;
    }
}

public sealed record CampaignVideoTextSummary(string Id, string Text, string Style);

public sealed record CampaignVideoShotSummary(
    int Shot,
    int StartMs,
    int DurationMs,
    string Shows,
    Guid? PhotoId,
    IReadOnlyList<CampaignVideoTextSummary> OnScreenText,
    string Voiceover);
