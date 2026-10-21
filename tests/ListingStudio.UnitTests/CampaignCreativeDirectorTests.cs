using ListingStudio.Application.Audio;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Videos;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class CampaignCreativeDirectorTests
{
    [Theory]
    [InlineData(1925, CampaignSoundtrack.HeritageWarm)]
    [InlineData(1975, CampaignSoundtrack.UpbeatLifestyle)]
    [InlineData(2024, CampaignSoundtrack.ModernAmbient)]
    [InlineData(null, CampaignSoundtrack.ModernAmbient)]
    public void AutoSoundtrackUsesHomeEra(int? yearBuilt, CampaignSoundtrack expected)
    {
        Assert.Equal(
            expected,
            CampaignCreativeDirector.ResolveSoundtrack(CampaignSoundtrack.AutoByHomeEra, yearBuilt));
    }

    [Fact]
    public void PopCaptionsAndAgentPortraitAreGroundedInExistingNarration()
    {
        var portraitId = Guid.NewGuid();
        var options = CampaignCreativeOptions.SocialDefault with
        {
            Elements = CampaignCreativeOptions.SocialDefault.Elements | CampaignVideoElement.AgentPortrait,
            AgentPortraitId = portraitId,
        };

        var result = CampaignCreativeDirector.Apply(CreateSpecification(), options, 2020);

        Assert.Equal(CampaignCreativeDirector.ModernAmbientAssetId, result.Audio.Music.AssetId);
        Assert.Equal($"agent-portrait:{portraitId:N}", result.Brand.SecondaryLogo);
        var captions = result.Scenes.SelectMany(scene => scene.TextOverlays)
            .Where(overlay => overlay.StyleToken == TextOverlayStyle.PopCaption)
            .ToArray();
        Assert.NotEmpty(captions);
        Assert.All(captions, caption => Assert.Equal("narration.fact", caption.GroundingKey));
        Assert.Contains(result.Scenes.SelectMany(scene => scene.LogoOverlays),
            overlay => overlay.AssetId == result.Brand.SecondaryLogo);
    }

    [Fact]
    public void CreativeOptionsRejectMusicOrPortraitMismatches()
    {
        Assert.Throws<ArgumentException>(() => (CampaignCreativeOptions.SocialDefault with
        {
            Soundtrack = CampaignSoundtrack.None,
        }).Validate());
        Assert.Throws<ArgumentException>(() => (CampaignCreativeOptions.SocialDefault with
        {
            Elements = CampaignCreativeOptions.SocialDefault.Elements | CampaignVideoElement.AgentPortrait,
        }).Validate());
    }

    [Fact]
    public void AgentAvatarRequiresOwnedPortraitConsentAndAddsPresenterOverlays()
    {
        var portraitId = Guid.NewGuid();
        var withoutConsent = CampaignCreativeOptions.SocialDefault with
        {
            Elements = CampaignCreativeOptions.SocialDefault.Elements | CampaignVideoElement.AgentAvatar,
            AgentPortraitId = portraitId,
        };
        Assert.Throws<ArgumentException>(withoutConsent.Validate);

        var approved = withoutConsent with { AgentAvatarConsentConfirmed = true };
        var result = CampaignCreativeDirector.Apply(CreateSpecification(), approved, 2020);

        Assert.Equal($"agent-portrait:{portraitId:N}", result.Brand.SecondaryLogo);
        Assert.Contains(
            result.Scenes[0].LogoOverlays,
            overlay => overlay.AssetId == result.Brand.SecondaryLogo && overlay.StartOffsetMs == 0);
    }

    [Fact]
    public void PriceDropTemplateRequiresAgentVerificationAndAddsGroundedHook()
    {
        var unverified = CampaignCreativeOptions.SocialDefault with
        {
            MarketingTemplate = CampaignMarketingTemplate.PriceDrop,
            PriceReductionAmount = 235_000m,
        };
        Assert.Throws<ArgumentException>(unverified.Validate);

        var options = unverified with { PriceReductionVerified = true };
        var result = CampaignCreativeDirector.Apply(CreateSpecification(), options, 2020);

        var binding = Assert.Single(result.FactBindings,
            item => item.Source == FactSource.AgentVerifiedCampaignInput);
        Assert.Equal("PRICE REDUCED $235,000", binding.Value);
        var hook = Assert.Single(result.Scenes[0].TextOverlays,
            overlay => overlay.Id == "campaign-price-reduction");
        Assert.Equal(binding.Key, hook.GroundingKey);
        Assert.Equal(TextOverlayStyle.OpeningTitle, hook.StyleToken);
    }

    [Fact]
    public void QuickTourTemplateUsesCutsAndOmitsPropertyFactPreset()
    {
        var options = CampaignCreativeOptions.ForTemplate(CampaignMarketingTemplate.QuickTour);
        var source = CreateSpecification();
        source = source with
        {
            Scenes = [source.Scenes[0] with
            {
                TransitionIn = new TransitionPlan(TransitionKind.Crossfade, 500),
            }],
        };
        var result = CampaignCreativeDirector.Apply(source, options, 2020);

        Assert.All(result.Scenes, scene => Assert.Equal(TransitionKind.Cut, scene.TransitionIn.Type));
        Assert.False(options.Elements.HasFlag(CampaignVideoElement.PropertyFacts));
    }

    [Fact]
    public void CaptionsShowANarrationThatSpansScenesOnceWhileTheVoiceIsHeard()
    {
        var source = CreateSpecification();
        var narration = source.Audio.NarrationSegments[0] with { StartMs = 0, DurationMs = 8_000 };
        var first = source.Scenes[0] with { NarrationSegmentIds = [narration.Id] };
        source = source with
        {
            Audio = source.Audio with { NarrationSegments = [narration] },
            Scenes = [first, first with { SceneNumber = 2, StartMs = 4_000 }],
        };
        var timing = new VoiceTimingMetadata([], [new VoiceSegmentTiming(narration.Id, 0, 6_000)]);

        var result = CampaignCreativeDirector.Apply(
            source, CampaignCreativeOptions.SocialDefault, 2020, narrationTiming: timing);

        var captions = result.Scenes
            .SelectMany(scene => scene.TextOverlays
                .Where(overlay => overlay.StyleToken == TextOverlayStyle.PopCaption)
                .Select(overlay => (Scene: scene, Overlay: overlay)))
            .OrderBy(item => item.Scene.StartMs + item.Overlay.StartOffsetMs)
            .ToArray();
        Assert.Equal(
            narration.Text.ToUpperInvariant(),
            string.Join(' ', captions.Select(item => item.Overlay.Text)));
        Assert.All(result.Scenes, scene => Assert.Contains(scene.TextOverlays,
            overlay => overlay.StyleToken == TextOverlayStyle.PopCaption));
        Assert.All(captions, item =>
        {
            Assert.True(item.Overlay.Text.Length <= 20);
            Assert.True(item.Scene.StartMs + item.Overlay.StartOffsetMs < 6_000);
            Assert.True(item.Overlay.StartOffsetMs + item.Overlay.DurationMs <= item.Scene.DurationMs);
        });
    }

    private static VideoProductionSpecification CreateSpecification()
    {
        var mediaId = Guid.NewGuid();
        var narration = new NarrationSegment("n1", 0, 4_000, "A bright kitchen opens into generous living space", "narration.fact");
        return new VideoProductionSpecification(
            "1.0",
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            RequestedDuration.Feature30,
            VideoAspectRatio.Vertical9By16,
            new VideoOutputProfile(1080, 1920, 30, "h264", "aac", "yuv420p", 48_000, 2),
            new NormalizedRect(0.05m, 0.05m, 0.90m, 0.90m),
            [new FactBinding("narration.fact", narration.Text, FactSource.PropertyStory, "story")],
            new BrandKit(null, null, "Agent", null, null, null, "#FFFFFF", "#17324D"),
            new GroundedText("Book a tour", "cta"),
            new AudioPlan([narration], new MusicPlan(null, MusicMood.None, 0, 0, 0, 0, 0, 0)),
            [new VideoScene(
                1,
                0,
                4_000,
                new VisualSource(VisualSourceKind.PropertyMedia, mediaId, null, null, null),
                new TransitionPlan(TransitionKind.Cut, 0),
                new MotionPlan(
                    MotionKind.KenBurns,
                    new NormalizedRect(0, 0, 1, 1),
                    new NormalizedRect(0.02m, 0.02m, 0.96m, 0.96m),
                    MotionEasing.EaseInOut),
                [],
                [],
                [narration.Id])]);
    }
}
