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
        IReadOnlyDictionary<string, string>? captionTextBySegmentId = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var scenes = source.Scenes.Select(scene => ApplyScene(
            scene,
            source,
            options,
            captionTextBySegmentId)).ToArray();
        var factBindings = source.FactBindings.ToList();
        if (options.MarketingTemplate == CampaignMarketingTemplate.PriceDrop)
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
        var soundtrack = ResolveSoundtrack(options.Soundtrack, yearBuilt);
        var music = soundtrack == CampaignSoundtrack.None
            ? new MusicPlan(null, MusicMood.None, 0, 0, 0, 0, 0, 0)
            : new MusicPlan(
                SoundtrackAssetId(soundtrack),
                SoundtrackMood(soundtrack),
                0,
                (int)source.RequestedDuration * 1_000,
                -20,
                900,
                1_400,
                -13);
        var portraitAssetId = options.AgentPortraitId is { } portraitId
            ? $"agent-portrait:{portraitId:N}"
            : null;
        if (portraitAssetId is not null && scenes.Length > 0)
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

        return source with
        {
            FactBindings = factBindings,
            Brand = source.Brand with { SecondaryLogo = portraitAssetId ?? source.Brand.SecondaryLogo },
            Audio = source.Audio with { Music = music },
            Scenes = scenes,
        };
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
        CampaignCreativeOptions options,
        IReadOnlyDictionary<string, string>? captionTextBySegmentId)
    {
        var overlays = scene.TextOverlays.Where(overlay => overlay.StyleToken switch
        {
            TextOverlayStyle.OpeningTitle => options.Elements.HasFlag(CampaignVideoElement.OpeningTitle),
            TextOverlayStyle.PropertyFact => options.Elements.HasFlag(CampaignVideoElement.PropertyFacts)
                && (options.Elements.HasFlag(CampaignVideoElement.NeighborhoodHighlights)
                    || !IsNeighborhoodBinding(source, overlay.GroundingKey)),
            TextOverlayStyle.ClosingCta => options.Elements.HasFlag(CampaignVideoElement.ClosingCallToAction),
            _ => true,
        }).ToList();

        if (options.Elements.HasFlag(CampaignVideoElement.NarrationCaptions))
        {
            foreach (var segmentId in scene.NarrationSegmentIds)
            {
                var segment = source.Audio.NarrationSegments.FirstOrDefault(item => item.Id == segmentId);
                if (segment is null || string.IsNullOrWhiteSpace(segment.Text))
                {
                    continue;
                }

                var captionText = captionTextBySegmentId is not null
                    && captionTextBySegmentId.TryGetValue(segment.Id, out var localizedCaption)
                    ? localizedCaption
                    : segment.Text;
                var words = captionText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var chunks = words.Chunk(5).ToArray();
                var segmentStart = Math.Max(scene.StartMs, segment.StartMs);
                var segmentEnd = Math.Min(
                    scene.StartMs + scene.DurationMs,
                    segment.StartMs + segment.DurationMs);
                var localStart = Math.Max(0, segmentStart - scene.StartMs);
                var localDuration = Math.Max(1, segmentEnd - segmentStart);
                for (var index = 0; index < chunks.Length; index++)
                {
                    var start = localStart + index * Math.Max(1, localDuration / chunks.Length);
                    var end = localStart + (index + 1) * localDuration / chunks.Length;
                    overlays.Add(new TextOverlay(
                        $"caption-{scene.SceneNumber}-{segment.Id}-{index}",
                        string.Join(' ', chunks[index]).ToUpperInvariant(),
                        segment.GroundingKey,
                        start,
                        Math.Max(250, end - start),
                        OverlayAnchor.BottomCenter,
                        new NormalizedRect(0.08m, 0.72m, 0.84m, 0.18m),
                        options.CaptionStyle == CampaignCaptionStyle.Pop
                            ? TextOverlayStyle.PopCaption
                            : TextOverlayStyle.LowerThird));
                }
            }
        }

        var visual = scene.VisualSource;
        if (!options.Elements.HasFlag(CampaignVideoElement.WalkthroughMotion)
            && visual.Kind == VisualSourceKind.PropertyVideo
            && visual.FallbackPropertyMediaId is { } fallbackId)
        {
            visual = new VisualSource(VisualSourceKind.PropertyMedia, fallbackId, null, null, null);
        }

        return scene with { VisualSource = visual, TextOverlays = overlays };
    }

    private static bool IsNeighborhoodBinding(VideoProductionSpecification source, string key) =>
        source.FactBindings.Any(binding => binding.Key == key && binding.Source == FactSource.ApprovedNeighborhood);
}
