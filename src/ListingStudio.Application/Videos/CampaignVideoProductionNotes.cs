using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Videos;

namespace ListingStudio.Application.Videos;

/// <summary>One plain-language fact about how a campaign video was made, for the video's chat to explain.</summary>
public sealed record CampaignVideoProductionNote(string Topic, string Detail);

/// <summary>
/// Describes how one finished campaign video was produced: the format and template, where the voiceover words came
/// from, the voice, music and captions, and which kinds of shots it uses. Built only from the stored plan and the
/// campaign's options, so the chat can answer "how was this made?" without guessing.
/// </summary>
public static class CampaignVideoProductionNotes
{
    public static IReadOnlyList<CampaignVideoProductionNote> Describe(
        CampaignOutputKind kind,
        VideoProductionSpecification specification,
        CampaignGenerationJob job,
        CampaignVoiceAccent? currentVoice)
    {
        ArgumentNullException.ThrowIfNull(specification);
        ArgumentNullException.ThrowIfNull(job);
        var notes = new List<CampaignVideoProductionNote>
        {
            new("How it was edited", Format(kind, job)),
            new("Marketing template", TemplateName(job.MarketingTemplate)),
        };

        var segments = specification.Audio.NarrationSegments;
        if (segments.Count == 0)
        {
            notes.Add(new("Voiceover", IsShortForm(kind) && job.ShortFormVoiceover == CampaignVoiceoverMode.None
                ? "None: the campaign was set to music and on-screen text only for short videos."
                : "None in this version."));
        }
        else
        {
            notes.Add(new("Voiceover words", ScriptSource(specification)));
            notes.Add(new("Voice", $"An AI voice speaking {job.SpokenLanguage} with a {Words((currentVoice ?? job.VoiceAccent).ToString())} accent, chosen in the campaign's language settings."));
        }

        notes.Add(new("Music", Music(specification, job)));
        var captionStyle = CampaignVideoEditor.CaptionStyleOf(specification);
        notes.Add(new("Captions", captionStyle is null
            ? "No captions in this version."
            : $"{(captionStyle == CampaignCaptionStyle.Pop ? "Pop" : "Classic")} captions in {job.CaptionLanguage}, timed to each spoken line."));
        notes.Add(new("Shots", Shots(specification)));
        notes.Add(new("Which photos can appear", "Only photos placed on the Tour Map, in walking order. Extra photos and unsorted photos are left out until they are placed on a stop."));
        var elements = Enum.GetValues<CampaignVideoElement>()
            .Where(element => element != CampaignVideoElement.None && job.CreativeElements.HasFlag(element))
            .Select(element => Words(element.ToString()).ToLowerInvariant())
            .ToArray();
        if (elements.Length > 0)
        {
            notes.Add(new("On-screen elements switched on", string.Join(", ", elements) + "."));
        }

        notes.Add(new("Facts", "Every price, bed and bath count, address and other fact on screen or spoken comes from the verified property details, story or approved script; the editor checks each one before rendering."));
        if (job.CompletedAtUtc is { } completed)
        {
            notes.Add(new("Campaign finished", completed.ToString("MMMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture)));
        }

        return notes;
    }

    public static bool IsShortForm(CampaignOutputKind kind) =>
        kind is CampaignOutputKind.TikTok or CampaignOutputKind.InstagramReel or CampaignOutputKind.YouTubeShort;

    private static string Format(CampaignOutputKind kind, CampaignGenerationJob job) => kind switch
    {
        _ when IsShortForm(kind) => job.Pace == CampaignPace.Cinematic
            ? "Vertical 9:16 short video made by the short-form editor in the Cinematic glide style: longer gliding shots joined by walk-through clips, opening on the price and location and ending on the agent's contact card."
            : "Vertical 9:16 short video made by the short-form editor in the Fast cuts style: a cut on the music beat every one to two seconds, every shot moving, opening on the price and location and ending on the agent's contact card.",
        CampaignOutputKind.YouTubeTour or CampaignOutputKind.FacebookFeed =>
            "A cut of the campaign's master tour plan. The AI director planned the master tour from the Tour Map photos and the voiceover, and this video keeps its shots, timing and voiceover.",
        CampaignOutputKind.FullNarratedTour or CampaignOutputKind.LongForm =>
            "A landscape tour planned by the AI director around the full voiceover, one stop after another in Tour Map order, so it runs as long as the narration.",
        _ => "A landscape video planned by the AI director from the Tour Map photos and the voiceover.",
    };

    private static string ScriptSource(VideoProductionSpecification specification)
    {
        var bindings = specification.FactBindings
            .GroupBy(binding => binding.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var sources = specification.Audio.NarrationSegments
            .Select(segment => bindings.TryGetValue(segment.GroundingKey, out var binding) ? binding : null)
            .OfType<FactBinding>()
            .ToArray();
        if (sources.Any(binding => binding.SourceReference.StartsWith("WalkthroughStopNarration:", StringComparison.Ordinal)))
        {
            return "Read word for word from the agent's room-by-room narration on the walkthrough map, each line starting as the video reaches that room.";
        }

        if (sources.Any(binding => binding.Source == FactSource.AcceptedScript))
        {
            return "Read word for word from the script the agent uploaded and accepted for marketing.";
        }

        return specification.Audio.NarrationSegments.Any(segment => segment.Id.StartsWith("short-", StringComparison.Ordinal))
            ? "A short voiceover written by AI for this cut from the approved property story."
            : "An AI script written from the approved property story.";
    }

    private static string Music(VideoProductionSpecification specification, CampaignGenerationJob job)
    {
        var track = CampaignVideoEditor.TrackOf(specification.Audio.Music);
        if (track == CampaignSoundtrack.None)
        {
            return job.Soundtrack == CampaignSoundtrack.AddInApp
                ? "No music in the file, so the agent can add a trending sound in TikTok or Instagram."
                : "No music.";
        }

        var name = track is { } known ? CampaignVideoEditor.TrackName(known) : "a built-in track";
        var why = job.Soundtrack == CampaignSoundtrack.AutoByHomeEra
            ? " Picked automatically for the home's era (older homes get Heritage warm, mid-century Upbeat lifestyle, newer homes Modern ambient)."
            : track == job.Soundtrack ? " Chosen on the Campaign tab." : " Changed in this video's chat.";
        var ducking = specification.Audio.NarrationSegments.Count > 0 ? ", lowered under the voiceover" : string.Empty;
        return $"The built-in {name} track at {specification.Audio.Music.GainDb.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} dB{ducking}.{why}";
    }

    private static string Shots(VideoProductionSpecification specification)
    {
        var scenes = specification.Scenes;
        var photos = scenes.Count(scene => scene.VisualSource.Kind is VisualSourceKind.PropertyMedia or VisualSourceKind.GenerativeMotionRequest);
        var clips = scenes.Count(scene => scene.VisualSource.Kind == VisualSourceKind.GeneratedClip);
        var videos = scenes.Count(scene => scene.VisualSource.Kind == VisualSourceKind.PropertyVideo);
        var parts = new List<string> { $"{scenes.Count} shots: {photos} listing photo{(photos == 1 ? string.Empty : "s")} with slow camera motion" };
        if (clips > 0)
        {
            parts.Add($"{clips} AI motion clip{(clips == 1 ? string.Empty : "s")} made from Tour Map photos (walk-through clips between neighboring stops, or camera moves generated for the campaign)");
        }

        if (videos > 0)
        {
            parts.Add($"{videos} moment{(videos == 1 ? string.Empty : "s")} from the agent's uploaded walkthrough video{(videos == 1 ? string.Empty : "s")}");
        }

        return string.Join(", ", parts) + ".";
    }

    private static string TemplateName(CampaignMarketingTemplate template) => template switch
    {
        CampaignMarketingTemplate.BalancedShowcase => "Balanced showcase",
        CampaignMarketingTemplate.PriceDrop => "Price drop",
        CampaignMarketingTemplate.QuickTour => "Quick tour",
        CampaignMarketingTemplate.FeatureSpotlight => "Feature spotlight",
        CampaignMarketingTemplate.AgentShowcase => "Agent showcase",
        CampaignMarketingTemplate.JustListed => "Just listed",
        CampaignMarketingTemplate.OpenHouse => "Open house",
        _ => Words(template.ToString()),
    };

    private static string Words(string value) => string.Concat(value.Select((character, index) =>
        index > 0 && char.IsUpper(character) ? $" {character}" : character.ToString()));
}
