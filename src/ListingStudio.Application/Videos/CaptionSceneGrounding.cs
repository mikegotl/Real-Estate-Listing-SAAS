using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ListingStudio.Domain.Videos;

namespace ListingStudio.Application.Videos;

/// <summary>
/// Which scenes a narration caption may appear over. A caption is room-specific when its line names a room (the
/// kitchen, the primary bedroom, the patio); such a caption may only sit on a scene that shows that room, or the photo
/// the director placed the line on in the tour. Lines that name no room ("Welcome home") stay general and can sit on
/// any scene. Room words are English; captions in other languages are room-specific only through the tour photo the
/// line was planned on.
/// </summary>
public sealed partial class CaptionSceneGrounding
{
    private static readonly (string Room, string Pattern)[] RoomWords =
    [
        ("kitchen", @"kitchen|kitchenette"),
        ("living", @"living room|living area|living space|family room|great room|den|lounge"),
        ("dining", @"dining room|dining area|dining|breakfast nook"),
        ("bedroom", @"bedroom|primary suite|master suite|guest room|nursery"),
        ("bathroom", @"bathroom|ensuite|en suite|powder room|primary bath|walk in shower|walk-in shower|bathtub|vanity"),
        ("office", @"home office|office|study"),
        ("laundry", @"laundry room|laundry|mudroom|mud room"),
        ("garage", @"garage"),
        ("basement", @"basement"),
        ("entry", @"foyer|entryway|entry hall|entry|front door|hallway"),
        ("sunroom", @"sunroom|sun room"),
        ("outdoor", @"back patio|patio|deck|porch|backyard|back yard|front yard|yard|garden|pool|exterior|curb appeal"),
    ];

    private static readonly Regex[] RoomPatterns = RoomWords
        .Select(word => new Regex(
            $@"(?<!\b(?:\d+(?:\.\d+)?|two|three|four|five|six|seven|eight|nine|ten)\s+)\b(?:{word.Pattern})\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled))
        .ToArray();

    private readonly IReadOnlyDictionary<Guid, string> roomByMedia;
    private readonly IReadOnlyDictionary<string, IReadOnlySet<Guid>> mediaBySegment;

    private CaptionSceneGrounding(
        IReadOnlyDictionary<Guid, string> roomByMedia,
        IReadOnlyDictionary<string, IReadOnlySet<Guid>> mediaBySegment)
    {
        this.roomByMedia = roomByMedia;
        this.mediaBySegment = mediaBySegment;
    }

    /// <summary>No grounding: every caption is placed by timing alone, as before.</summary>
    public static CaptionSceneGrounding? None => null;

    /// <summary>
    /// Grounding from the tour plan, where the director put each narration line on the scenes it describes, and the
    /// room of each photo (its storyboard stop or recognised room type).
    /// </summary>
    public static CaptionSceneGrounding From(
        VideoProductionSpecification master,
        IReadOnlyDictionary<Guid, string?> roomByMedia)
    {
        ArgumentNullException.ThrowIfNull(master);
        var segments = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);
        foreach (var scene in master.Scenes)
        {
            foreach (var mediaId in MediaOf(scene))
            {
                foreach (var segmentId in scene.NarrationSegmentIds)
                {
                    if (!segments.TryGetValue(segmentId, out var set))
                    {
                        segments[segmentId] = set = [];
                    }

                    set.Add(mediaId);
                }
            }
        }

        return new CaptionSceneGrounding(
            Rooms(roomByMedia),
            segments.ToDictionary(pair => pair.Key, pair => (IReadOnlySet<Guid>)pair.Value, StringComparer.Ordinal));
    }

    /// <summary>Grounding from photo rooms only, for a video whose tour plan is no longer at hand.</summary>
    public static CaptionSceneGrounding FromRooms(IReadOnlyDictionary<Guid, string?> roomByMedia) =>
        new(Rooms(roomByMedia), new Dictionary<string, IReadOnlySet<Guid>>(StringComparer.Ordinal));

    /// <summary>The rooms a line names, as canonical room keys.</summary>
    public static IReadOnlySet<string> RoomsNamedIn(string? text)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        for (var index = 0; index < RoomWords.Length; index++)
        {
            if (RoomPatterns[index].IsMatch(text))
            {
                result.Add(RoomWords[index].Room);
            }
        }

        return result;
    }

    /// <summary>The canonical room of a storyboard stop or analysed room type, or null when it names none.</summary>
    public static string? CanonicalRoom(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        var rooms = RoomsNamedIn(label.Replace('_', ' ').Replace('-', ' '));
        return rooms.Count == 0 ? null : RoomWords.First(word => rooms.Contains(word.Room)).Room;
    }

    /// <summary>
    /// Whether a caption for this narration line may sit on the scene: always for a line that names no room and was
    /// not planned on a specific photo, otherwise only over that photo or a photo of a room the line names.
    /// </summary>
    public bool Fits(string segmentId, string spokenText, string? captionText, VideoScene scene)
    {
        var named = new HashSet<string>(RoomsNamedIn(spokenText), StringComparer.Ordinal);
        named.UnionWith(RoomsNamedIn(captionText));
        mediaBySegment.TryGetValue(segmentId, out var planned);
        if (named.Count == 0)
        {
            // A line in another language: fall back to the tour photo it was planned on, when that photo shows a room.
            if (planned is null || IsEnglishLike(spokenText) || !planned.Any(roomByMedia.ContainsKey))
            {
                return true;
            }

            return MediaOf(scene).Any(planned.Contains);
        }

        foreach (var mediaId in MediaOf(scene))
        {
            if (planned?.Contains(mediaId) == true
                || (roomByMedia.TryGetValue(mediaId, out var room) && named.Contains(room)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Every narration caption sitting over a scene it does not fit, as readable problems. Empty when the captions are
    /// grounded or there is no grounding to check against.
    /// </summary>
    public static IReadOnlyList<string> FindMismatches(
        VideoProductionSpecification specification,
        CaptionSceneGrounding? grounding)
    {
        ArgumentNullException.ThrowIfNull(specification);
        if (grounding is null)
        {
            return [];
        }

        var segments = specification.Audio.NarrationSegments
            .GroupBy(segment => segment.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var scene in specification.Scenes)
        {
            foreach (var overlay in scene.TextOverlays.Where(CampaignVideoEditor.IsCaption))
            {
                if (SegmentIdOf(overlay.Id) is not { } segmentId || !segments.TryGetValue(segmentId, out var segment))
                {
                    continue;
                }

                if (!grounding.Fits(segmentId, segment.Text, overlay.Text, scene))
                {
                    problems.Add($"Caption {overlay.Id} for line {segmentId} is over scene {scene.SceneNumber}, which shows another room.");
                }
            }
        }

        return problems;
    }

    /// <summary>The narration line of a caption overlay id ("caption-{scene}-{segment}-{chunk}").</summary>
    public static string? SegmentIdOf(string overlayId)
    {
        if (!overlayId.StartsWith("caption-", StringComparison.Ordinal))
        {
            return null;
        }

        var rest = overlayId["caption-".Length..];
        var first = rest.IndexOf('-', StringComparison.Ordinal);
        var last = rest.LastIndexOf('-');
        return first < 0 || last <= first ? null : rest[(first + 1)..last];
    }

    /// <summary>The photos a scene shows: its photo, or the photo an AI clip or fallback starts from.</summary>
    public static IEnumerable<Guid> MediaOf(VideoScene scene)
    {
        if (scene.VisualSource.PropertyMediaId is { } mediaId)
        {
            yield return mediaId;
        }

        if (scene.VisualSource.FallbackPropertyMediaId is { } fallbackId && fallbackId != scene.VisualSource.PropertyMediaId)
        {
            yield return fallbackId;
        }
    }

    /// <summary>
    /// Removes characters that draw nothing or break a single-line caption (zero-width and other format marks,
    /// control characters) and collapses runs of whitespace. Returns an empty string for a caption with no visible
    /// text, which callers skip.
    /// </summary>
    public static string CleanCaptionText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var character in text.Normalize(NormalizationForm.FormC))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (category is UnicodeCategory.Format)
            {
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static Dictionary<Guid, string> Rooms(IReadOnlyDictionary<Guid, string?> roomByMedia)
    {
        ArgumentNullException.ThrowIfNull(roomByMedia);
        var result = new Dictionary<Guid, string>();
        foreach (var (mediaId, label) in roomByMedia)
        {
            if (CanonicalRoom(label) is { } room)
            {
                result[mediaId] = room;
            }
        }

        return result;
    }

    private static bool IsEnglishLike(string text) => EnglishFunctionWords().IsMatch(text);

    [GeneratedRegex(@"\b(?:the|and|with|this|is|to|of|a|in|for|your|you)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EnglishFunctionWords();
}
