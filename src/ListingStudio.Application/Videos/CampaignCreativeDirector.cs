using ListingStudio.Application.Audio;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Videos;

namespace ListingStudio.Application.Videos;

public static class CampaignCreativeDirector
{
    public const string HeritageWarmAssetId = "listing-studio:heritage-warm:v1";
    public const string ModernAmbientAssetId = "listing-studio:modern-ambient:v1";
    public const string UpbeatLifestyleAssetId = "listing-studio:upbeat-lifestyle:v1";

    public static VideoProductionSpecification Apply(
        VideoProductionSpecification source,
        CampaignCreativeOptions options,
        int? yearBuilt,
        IReadOnlyDictionary<string, string>? captionTextBySegmentId = null,
        VoiceTimingMetadata? narrationTiming = null,
        CaptionSceneGrounding? captionGrounding = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var scenes = source.Scenes.Select(scene => ApplyScene(scene, source, options)).ToArray();
        if (options.Elements.HasFlag(CampaignVideoElement.NarrationCaptions))
        {
            AddCaptions(source, options, scenes, captionTextBySegmentId, narrationTiming, captionGrounding);
        }

        var factBindings = source.FactBindings.ToList();
        // A short-form edit already carries the price-reduction pill in its hook lines.
        if (options.MarketingTemplate == CampaignMarketingTemplate.PriceDrop
            && !scenes.Any(scene => scene.TextOverlays.Any(overlay => overlay.StyleToken == TextOverlayStyle.HookTitle)))
        {
            ApplyPriceDropHook(source, options, scenes, factBindings);
        }

        if (options.MarketingTemplate == CampaignMarketingTemplate.QuickTour)
        {
            scenes = scenes.Select(scene => scene with
            {
                TransitionIn = new TransitionPlan(TransitionKind.Cut, 0),
            }).ToArray();
        }
        var music = MusicFor(ResolveSoundtrack(options.Soundtrack, yearBuilt), source);
        var usesPortrait = options.Elements.HasFlag(CampaignVideoElement.AgentPortrait)
            || options.Elements.HasFlag(CampaignVideoElement.AgentAvatar);
        var portraitAssetId = usesPortrait && options.AgentPortraitId is { } portraitId
            ? $"agent-portrait:{portraitId:N}"
            : null;
        if (portraitAssetId is not null
            && options.Elements.HasFlag(CampaignVideoElement.AgentPortrait)
            && scenes.Length > 0)
        {
            var closingIndex = scenes.Length - 1;
            var closing = scenes[closingIndex];
            scenes[closingIndex] = closing with
            {
                LogoOverlays = closing.LogoOverlays.Append(new LogoOverlay(
                    portraitAssetId,
                    Math.Max(0, closing.DurationMs - Math.Min(4_000, closing.DurationMs)),
                    Math.Min(4_000, closing.DurationMs),
                    source.AspectRatio == VideoAspectRatio.Vertical9By16
                        ? new NormalizedRect(0.68m, 0.66m, 0.24m, 0.24m)
                        : new NormalizedRect(0.78m, 0.60m, 0.16m, 0.28m),
                    0.95m)).ToArray(),
            };
        }

        if (portraitAssetId is not null
            && options.Elements.HasFlag(CampaignVideoElement.AgentAvatar)
            && scenes.Length > 0)
        {
            // The approved portrait acts as an on-screen presenter while the campaign's disclosed AI voice narrates.
            // It is deliberately not described as lip-synced: a protected talking-avatar provider is not invoked by
            // this deterministic renderer.
            var first = scenes[0];
            scenes[0] = first with
            {
                LogoOverlays = first.LogoOverlays.Append(new LogoOverlay(
                    portraitAssetId,
                    0,
                    Math.Min(6_000, first.DurationMs),
                    source.AspectRatio == VideoAspectRatio.Vertical9By16
                        ? new NormalizedRect(0.56m, 0.43m, 0.36m, 0.42m)
                        : new NormalizedRect(0.72m, 0.50m, 0.22m, 0.38m),
                    0.98m)).ToArray(),
            };
            if (scenes.Length > 1)
            {
                var closingIndex = scenes.Length - 1;
                var closing = scenes[closingIndex];
                scenes[closingIndex] = closing with
                {
                    LogoOverlays = closing.LogoOverlays.Append(new LogoOverlay(
                        portraitAssetId,
                        Math.Max(0, closing.DurationMs - Math.Min(4_000, closing.DurationMs)),
                        Math.Min(4_000, closing.DurationMs),
                        source.AspectRatio == VideoAspectRatio.Vertical9By16
                            ? new NormalizedRect(0.68m, 0.66m, 0.24m, 0.24m)
                            : new NormalizedRect(0.78m, 0.60m, 0.16m, 0.28m),
                        0.95m)).ToArray(),
                };
            }
        }

        return source with
        {
            FactBindings = factBindings,
            Brand = source.Brand with { SecondaryLogo = portraitAssetId ?? source.Brand.SecondaryLogo },
            Audio = source.Audio with { Music = music },
            Scenes = scenes,
        };
    }

    /// <summary>The music plan for a soundtrack over the whole program, or silence for none.</summary>
    public static MusicPlan MusicFor(CampaignSoundtrack soundtrack, VideoProductionSpecification specification) =>
        soundtrack is CampaignSoundtrack.None or CampaignSoundtrack.AddInApp or CampaignSoundtrack.AutoByHomeEra
            ? new MusicPlan(null, MusicMood.None, 0, 0, 0, 0, 0, 0)
            : new MusicPlan(
                SoundtrackAssetId(soundtrack),
                SoundtrackMood(soundtrack),
                0,
                (int)specification.RequestedDuration * 1_000,
                // Music carries a video with no voiceover, so it plays louder there.
                specification.Audio.NarrationSegments.Count == 0 ? 0 : -16,
                900,
                1_400,
                -13);

    /// <summary>
    /// Drops a video's captions and places them again from new voice timing, for a voiceover that was re-recorded.
    /// </summary>
    public static VideoProductionSpecification ReplaceCaptions(
        VideoProductionSpecification source,
        CampaignCaptionStyle style,
        IReadOnlyDictionary<string, string>? captionTextBySegmentId,
        VoiceTimingMetadata? narrationTiming,
        CaptionSceneGrounding? captionGrounding = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var scenes = source.Scenes
            .Select(scene => scene with
            {
                TextOverlays = scene.TextOverlays.Where(overlay => !CampaignVideoEditor.IsCaption(overlay)).ToArray(),
            })
            .ToArray();
        AddCaptions(
            source,
            CampaignCreativeOptions.SocialDefault with { CaptionStyle = style },
            scenes,
            captionTextBySegmentId,
            narrationTiming,
            captionGrounding);
        return source with { Scenes = scenes };
    }

    private static void ApplyPriceDropHook(
        VideoProductionSpecification source,
        CampaignCreativeOptions options,
        VideoScene[] scenes,
        List<FactBinding> factBindings)
    {
        if (scenes.Length == 0 || options.PriceReductionAmount is not { } amount)
        {
            return;
        }

        const string groundingKey = "campaign.priceReduction";
        var formattedAmount = amount.ToString("C0", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
        var hook = $"PRICE REDUCED {formattedAmount}";
        factBindings.RemoveAll(binding => binding.Key == groundingKey);
        factBindings.Add(new FactBinding(
            groundingKey,
            hook,
            FactSource.AgentVerifiedCampaignInput,
            "Agent-confirmed campaign input"));

        var first = scenes[0];
        var retainedLocation = first.TextOverlays
            .Where(overlay => overlay.StyleToken == TextOverlayStyle.OpeningTitle)
            .Take(1)
            .Select(overlay => overlay with
            {
                Id = $"{overlay.Id}-location",
                Box = source.AspectRatio == VideoAspectRatio.Vertical9By16
                    ? new NormalizedRect(0.14m, 0.30m, 0.72m, 0.12m)
                    : new NormalizedRect(0.20m, 0.28m, 0.60m, 0.12m),
                StyleToken = TextOverlayStyle.PropertyFact,
            });
        var overlays = first.TextOverlays
            .Where(overlay => overlay.StyleToken != TextOverlayStyle.OpeningTitle)
            .Concat(retainedLocation)
            .Prepend(new TextOverlay(
                "campaign-price-reduction",
                hook,
                groundingKey,
                0,
                Math.Min(4_000, first.DurationMs),
                OverlayAnchor.TopCenter,
                source.AspectRatio == VideoAspectRatio.Vertical9By16
                    ? new NormalizedRect(0.08m, 0.08m, 0.84m, 0.20m)
                    : new NormalizedRect(0.12m, 0.08m, 0.76m, 0.18m),
                TextOverlayStyle.OpeningTitle))
            .ToArray();
        scenes[0] = first with { TextOverlays = overlays };
    }

    /// <summary>The tempo of a library soundtrack; 120 BPM, the common short-video tempo, when there is none.</summary>
    public static int BeatsPerMinute(string? soundtrackAssetId) => soundtrackAssetId switch
    {
        HeritageWarmAssetId => 76,
        ModernAmbientAssetId => 92,
        UpbeatLifestyleAssetId => 112,
        _ => 120,
    };

    public static CampaignSoundtrack ResolveSoundtrack(CampaignSoundtrack selected, int? yearBuilt) =>
        selected != CampaignSoundtrack.AutoByHomeEra
            ? selected
            : yearBuilt switch
            {
                <= 1949 => CampaignSoundtrack.HeritageWarm,
                <= 1989 => CampaignSoundtrack.UpbeatLifestyle,
                _ => CampaignSoundtrack.ModernAmbient,
            };

    public static string SoundtrackAssetId(CampaignSoundtrack soundtrack) => soundtrack switch
    {
        CampaignSoundtrack.HeritageWarm => HeritageWarmAssetId,
        CampaignSoundtrack.ModernAmbient => ModernAmbientAssetId,
        CampaignSoundtrack.UpbeatLifestyle => UpbeatLifestyleAssetId,
        _ => throw new ArgumentOutOfRangeException(nameof(soundtrack)),
    };

    private static MusicMood SoundtrackMood(CampaignSoundtrack soundtrack) => soundtrack switch
    {
        CampaignSoundtrack.HeritageWarm => MusicMood.WarmCinematic,
        CampaignSoundtrack.ModernAmbient => MusicMood.ModernAmbient,
        CampaignSoundtrack.UpbeatLifestyle => MusicMood.UpbeatLifestyle,
        _ => MusicMood.None,
    };

    private static VideoScene ApplyScene(
        VideoScene scene,
        VideoProductionSpecification source,
        CampaignCreativeOptions options)
    {
        var overlays = scene.TextOverlays.Where(overlay => overlay.StyleToken switch
        {
            TextOverlayStyle.OpeningTitle => options.Elements.HasFlag(CampaignVideoElement.OpeningTitle),
            TextOverlayStyle.PropertyFact => options.Elements.HasFlag(CampaignVideoElement.PropertyFacts)
                && (options.Elements.HasFlag(CampaignVideoElement.NeighborhoodHighlights)
                    || !IsNeighborhoodBinding(source, overlay.GroundingKey)),
            TextOverlayStyle.ClosingCta => options.Elements.HasFlag(CampaignVideoElement.ClosingCallToAction),
            TextOverlayStyle.AgentContact => options.Elements.HasFlag(CampaignVideoElement.AgentContact),
            _ => true,
        }).ToList();

        var visual = scene.VisualSource;
        if (!options.Elements.HasFlag(CampaignVideoElement.WalkthroughMotion)
            && visual.Kind == VisualSourceKind.PropertyVideo
            && visual.FallbackPropertyMediaId is { } fallbackId)
        {
            visual = new VisualSource(VisualSourceKind.PropertyMedia, fallbackId, null, null, null);
        }

        return scene with { VisualSource = visual, TextOverlays = overlays };
    }

    /// <summary>Burst starts in proportion to their length, for a voice without per-word timing.</summary>
    private static int[] LengthWeightedChunkStarts(string[] chunks, int spokenMs)
    {
        var totalLength = chunks.Sum(chunk => chunk.Length + 1);
        var starts = new int[chunks.Length];
        var offset = 0;
        for (var index = 0; index < chunks.Length; index++)
        {
            starts[index] = (int)((long)spokenMs * offset / totalLength);
            offset += chunks[index].Length + 1;
        }

        return starts;
    }

    /// <summary>
    /// Burst starts from the voice's per-character timing, so each burst appears as its first word is spoken. Only
    /// when the captions are the spoken words; translated captions keep the length-weighted timing.
    /// </summary>
    private static int[]? WordTimedChunkStarts(
        string[] chunks,
        string captionText,
        string spokenText,
        VoiceTimingMetadata? timing,
        VoiceSegmentTiming? audio)
    {
        if (timing is null || audio is null || timing.Characters.Count == 0
            || !string.Equals(
                CaptionSceneGrounding.CleanCaptionText(captionText),
                CaptionSceneGrounding.CleanCaptionText(spokenText),
                StringComparison.Ordinal))
        {
            return null;
        }

        var wordStarts = new List<int>();
        var inWord = false;
        foreach (var mark in timing.Characters.Where(mark =>
                     mark.StartMs >= audio.StartMs && mark.EndMs <= audio.EndMs))
        {
            var isSpace = string.IsNullOrWhiteSpace(mark.Character);
            if (!isSpace && !inWord)
            {
                wordStarts.Add(mark.StartMs - audio.StartMs);
            }

            inWord = !isSpace;
        }

        var wordCount = chunks.Sum(chunk => CaptionWords(chunk).Length);
        if (wordStarts.Count != wordCount)
        {
            return null;
        }

        var starts = new int[chunks.Length];
        var word = 0;
        for (var index = 0; index < chunks.Length; index++)
        {
            starts[index] = wordStarts[word];
            word += CaptionWords(chunks[index]).Length;
        }

        return starts;
    }

    // Provider narration and accepted scripts may contain paragraphs or tabs. Caption overlays
    // are single-line text, and the renderer correctly rejects control characters in overlays.
    private static string[] CaptionWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static string[] ChunkCaption(string text, int maximumCharacters)
    {
        var chunks = new List<string>();
        var current = new List<string>();
        var length = 0;
        foreach (var word in CaptionWords(text))
        {
            if (current.Count > 0 && (current.Count == 5 || length + 1 + word.Length > maximumCharacters))
            {
                chunks.Add(string.Join(' ', current));
                current.Clear();
                length = 0;
            }

            length += (current.Count > 0 ? 1 : 0) + word.Length;
            current.Add(word);
        }

        if (current.Count > 0)
        {
            chunks.Add(string.Join(' ', current));
        }

        return [.. chunks];
    }

    /// <summary>
    /// Shows each narration segment once, in short bursts of up to five words spread over the window where the voice is heard. A
    /// segment often spans several scenes, so each burst goes to the scene it starts in. Bursts get time in
    /// proportion to their length, which tracks speech more closely than equal slots.
    /// </summary>
    private static void AddCaptions(
        VideoProductionSpecification source,
        CampaignCreativeOptions options,
        VideoScene[] scenes,
        IReadOnlyDictionary<string, string>? captionTextBySegmentId,
        VoiceTimingMetadata? narrationTiming,
        CaptionSceneGrounding? captionGrounding)
    {
        var programMs = (int)source.RequestedDuration * 1_000;
        var segments = source.Audio.NarrationSegments.ToDictionary(segment => segment.Id, StringComparer.Ordinal);
        var overlays = scenes.Select(scene => scene.TextOverlays.ToList()).ToArray();
        // Captions are one unwrapped line, so a vertical frame takes fewer words per burst. They also sit above the
        // closing call to action and the platform's own controls at the bottom of a phone screen.
        var vertical = source.AspectRatio == VideoAspectRatio.Vertical9By16;
        var captionBox = vertical
            ? new NormalizedRect(0.08m, 0.56m, 0.84m, 0.12m)
            : new NormalizedRect(0.08m, 0.72m, 0.84m, 0.18m);
        foreach (var placed in NarrationPlacement.Place(source.Audio.NarrationSegments, narrationTiming, programMs))
        {
            var segment = segments[placed.SegmentId];
            // Blank, whitespace-only or invisible caption text never reaches FFmpeg, which rejects it.
            var captionText = CaptionSceneGrounding.CleanCaptionText(captionTextBySegmentId is not null
                && captionTextBySegmentId.TryGetValue(segment.Id, out var localizedCaption)
                ? localizedCaption
                : segment.Text);
            if (captionText.Length == 0)
            {
                continue;
            }

            var audio = narrationTiming?.Segments.FirstOrDefault(item => item.SegmentId == segment.Id);
            var spokenMs = audio is null ? placed.DurationMs : audio.EndMs - audio.StartMs;
            var chunks = ChunkCaption(captionText, vertical ? 20 : 36);
            var chunkStarts = WordTimedChunkStarts(chunks, captionText, segment.Text, narrationTiming, audio)
                ?? LengthWeightedChunkStarts(chunks, spokenMs);
            for (var index = 0; index < chunks.Length; index++)
            {
                var startMs = placed.StartMs + chunkStarts[index];
                var endMs = Math.Min(
                    placed.StartMs + placed.DurationMs,
                    placed.StartMs + (index + 1 < chunks.Length ? chunkStarts[index + 1] : spokenMs));
                if (startMs >= placed.StartMs + placed.DurationMs || startMs >= programMs)
                {
                    break;
                }

                var sceneIndex = Array.FindLastIndex(scenes, scene => scene.StartMs <= startMs);
                var scene = scenes[sceneIndex];
                // A caption about one room is shown only over that room: when the edit has cut to another room by
                // the time these words are spoken, the voice carries on without the caption.
                if (captionGrounding is not null && !captionGrounding.Fits(segment.Id, segment.Text, captionText, scene))
                {
                    continue;
                }
                var localStart = startMs - scene.StartMs;
                var duration = Math.Min(Math.Max(250, endMs - startMs), scene.DurationMs - localStart);
                if (duration <= 0)
                {
                    continue;
                }

                overlays[sceneIndex].Add(new TextOverlay(
                    $"caption-{scene.SceneNumber}-{segment.Id}-{index}",
                    chunks[index].ToUpperInvariant(),
                    segment.GroundingKey,
                    localStart,
                    duration,
                    OverlayAnchor.BottomCenter,
                    captionBox,
                    options.CaptionStyle == CampaignCaptionStyle.Pop
                        ? TextOverlayStyle.PopCaption
                        : TextOverlayStyle.LowerThird));
            }
        }

        for (var index = 0; index < scenes.Length; index++)
        {
            scenes[index] = scenes[index] with { TextOverlays = overlays[index] };
        }
    }

    private static bool IsNeighborhoodBinding(VideoProductionSpecification source, string key) =>
        source.FactBindings.Any(binding => binding.Key == key && binding.Source == FactSource.ApprovedNeighborhood);
}

