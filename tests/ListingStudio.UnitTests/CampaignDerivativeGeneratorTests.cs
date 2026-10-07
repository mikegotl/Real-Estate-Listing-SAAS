using ListingStudio.Application.Videos;
using ListingStudio.Domain.Videos;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class CampaignDerivativeGeneratorTests
{
    [Fact]
    public void GenerateCreatesThreeDurationsInBothAspectRatios()
    {
        var fixture = CreateFixture();

        var result = new CampaignDerivativeGenerator().Generate(fixture.Request);

        Assert.Equal(6, result.Derivatives.Count);
        Assert.Equal(6, result.Derivatives.Select(item => (item.Kind, item.AspectRatio)).Distinct().Count());
        foreach (var derivative in result.Derivatives)
        {
            var expectedDuration = derivative.Kind switch
            {
                CampaignDeliverableKind.Hero => 60_000,
                CampaignDeliverableKind.Feature => 30_000,
                CampaignDeliverableKind.Teaser => 15_000,
                _ => throw new ArgumentOutOfRangeException(),
            };
            var expectedStart = 0;
            foreach (var scene in derivative.Specification.Scenes)
            {
                Assert.Equal(expectedStart, scene.StartMs);
                Assert.True(scene.DurationMs > 0);
                expectedStart += scene.DurationMs;
            }

            Assert.Equal(expectedDuration, expectedStart);
            Assert.Equal(expectedDuration / 1_000, (int)derivative.Specification.RequestedDuration);
            Assert.Contains(derivative.Specification.Scenes[^1].TextOverlays, overlay =>
                overlay.StyleToken == TextOverlayStyle.ClosingCta
                && overlay.Text == fixture.Master.CallToAction.Text);
        }
    }

    [Fact]
    public void GenerateReusesMasterDecisionsAndExpensiveAssets()
    {
        var fixture = CreateFixture();

        var result = new CampaignDerivativeGenerator().Generate(fixture.Request);
        var teaser = result.Get(CampaignDeliverableKind.Teaser, VideoAspectRatio.Landscape16By9);
        var feature = result.Get(CampaignDeliverableKind.Feature, VideoAspectRatio.Landscape16By9);

        Assert.Equal(3, teaser.Specification.Scenes.Count);
        Assert.Equal(6, feature.Specification.Scenes.Count);
        Assert.Equal(fixture.Master.PropertyId, teaser.Specification.PropertyId);
        Assert.Equal(fixture.Master.PropertyStoryId, teaser.Specification.PropertyStoryId);
        Assert.Equal(fixture.Master.PropertyStoryVersion, teaser.Specification.PropertyStoryVersion);
        Assert.Same(fixture.Master.FactBindings, teaser.Specification.FactBindings);
        Assert.Equal(fixture.GeneratedClipId, Assert.Single(teaser.ReusedGeneratedClipIds));
        Assert.Equal(fixture.GeneratedClipId, Assert.Single(feature.ReusedGeneratedClipIds));
        Assert.Contains(teaser.Specification.Scenes, scene =>
            scene.VisualSource.GeneratedClipId == fixture.GeneratedClipId
            && scene.VisualSource.FallbackPropertyMediaId == fixture.MediaId);
        Assert.Equal("licensed-music", teaser.Specification.Audio.Music.AssetId);
        Assert.Equal(15_000, teaser.Specification.Audio.Music.DurationMs);
        Assert.All(teaser.Specification.Audio.NarrationSegments, segment =>
            Assert.Contains(fixture.Master.Audio.NarrationSegments, source => source.Id == segment.Id));
    }

    [Fact]
    public void GenerateReframesVerticalMediaAndKeepsBrandingInsideSafeZone()
    {
        var fixture = CreateFixture();

        var vertical = new CampaignDerivativeGenerator().Generate(fixture.Request)
            .Get(CampaignDeliverableKind.Feature, VideoAspectRatio.Vertical9By16)
            .Specification;

        Assert.Equal(new VideoOutputProfile(1_080, 1_920, 30, "h264", "aac", "yuv420p", 48_000, 2), vertical.Output);
        Assert.Equal(new NormalizedRect(0.075m, 0.05m, 0.85m, 0.9m), vertical.SafeZone);
        foreach (var scene in vertical.Scenes)
        {
            AssertViewportAspect(scene.Motion.StartViewport, fixture.MediaWidth, fixture.MediaHeight, 9m / 16m);
            AssertViewportAspect(scene.Motion.EndViewport, fixture.MediaWidth, fixture.MediaHeight, 9m / 16m);
            Assert.All(scene.TextOverlays, overlay => AssertInside(overlay.Box, vertical.SafeZone));
            Assert.All(scene.LogoOverlays, overlay => AssertInside(overlay.Box, vertical.SafeZone));
        }
    }

    [Fact]
    public void GenerateRejectsInvalidMasterOrDuplicateMedia()
    {
        var fixture = CreateFixture();
        var invalidMaster = fixture.Master with
        {
            Scenes = fixture.Master.Scenes.Select((scene, index) =>
                index == 1 ? scene with { StartMs = scene.StartMs + 1 } : scene).ToArray(),
        };
        var duplicateMedia = fixture.Request with
        {
            PropertyMedia = [.. fixture.Request.PropertyMedia, fixture.Request.PropertyMedia[0]],
        };

        Assert.Throws<ArgumentException>(() => new CampaignDerivativeGenerator().Generate(
            fixture.Request with { MasterSpecification = invalidMaster }));
        Assert.Throws<ArgumentException>(() => new CampaignDerivativeGenerator().Generate(duplicateMedia));
    }

    [Fact]
    public void GeneratePreservesNarrationThatSpansMultipleScenes()
    {
        var fixture = CreateFixture();
        var spanning = new NarrationSegment(
            "narration-spanning",
            1_000,
            39_000,
            "A continuous narration across the opening scenes.",
            "story.voiceover");
        var scenes = fixture.Master.Scenes.Select(scene => scene with
        {
            NarrationSegmentIds = scene.StartMs < spanning.StartMs + spanning.DurationMs
                && scene.StartMs + scene.DurationMs > spanning.StartMs
                    ? [spanning.Id]
                    : [],
        }).ToArray();
        var master = fixture.Master with
        {
            Audio = fixture.Master.Audio with { NarrationSegments = [spanning] },
            Scenes = scenes,
        };

        var result = new CampaignDerivativeGenerator().Generate(
            fixture.Request with { MasterSpecification = master });

        foreach (var derivative in result.Derivatives)
        {
            var narration = Assert.Single(derivative.Specification.Audio.NarrationSegments);
            Assert.Equal(spanning.Id, narration.Id);
            Assert.True(narration.StartMs >= 0);
            Assert.True(narration.DurationMs > 0);
            Assert.True(
                narration.StartMs + narration.DurationMs
                <= (int)derivative.Specification.RequestedDuration * 1_000);
            Assert.Contains(
                derivative.Specification.Scenes,
                scene => scene.NarrationSegmentIds.Contains(spanning.Id, StringComparer.Ordinal));
        }
    }

    private static Fixture CreateFixture()
    {
        const int width = 1_600;
        const int height = 900;
        var mediaId = Guid.NewGuid();
        var generatedClipId = Guid.NewGuid();
        var scenes = Enumerable.Range(0, 8).Select(index =>
        {
            var isGenerated = index == 3;
            var isLast = index == 7;
            var overlays = new List<TextOverlay>();
            if (index == 0)
            {
                overlays.Add(new TextOverlay(
                    "opening", "123 Main Street", "property.address.full", 500, 2_000,
                    OverlayAnchor.TopCenter, new NormalizedRect(0.15m, 0.08m, 0.7m, 0.12m),
                    TextOverlayStyle.OpeningTitle));
            }
            if (isGenerated)
            {
                overlays.Add(new TextOverlay(
                    "feature", "A cinematic welcome", "story.feature", 500, 2_000,
                    OverlayAnchor.BottomLeft, new NormalizedRect(0.08m, 0.75m, 0.5m, 0.1m),
                    TextOverlayStyle.LowerThird));
            }
            if (isLast)
            {
                overlays.Add(new TextOverlay(
                    "closing", "Contact the listing team.", "story.closingCta", 4_500, 2_000,
                    OverlayAnchor.BottomCenter, new NormalizedRect(0.2m, 0.78m, 0.6m, 0.1m),
                    TextOverlayStyle.ClosingCta));
            }

            return new VideoScene(
                index + 1,
                index * 7_500,
                7_500,
                isGenerated
                    ? new VisualSource(VisualSourceKind.GeneratedClip, null, generatedClipId, mediaId, null)
                    : new VisualSource(VisualSourceKind.PropertyMedia, mediaId, null, null, null),
                index == 0
                    ? new TransitionPlan(TransitionKind.Cut, 0)
                    : new TransitionPlan(TransitionKind.Crossfade, 500),
                new MotionPlan(
                    MotionKind.KenBurns,
                    new NormalizedRect(0, 0, 1, 1),
                    new NormalizedRect(0.04m, 0.04m, 0.92m, 0.92m),
                    MotionEasing.EaseInOut),
                overlays,
                index is 0 or 3 or 7
                    ? [new LogoOverlay("agent-logo", 500, 2_000, new NormalizedRect(0.8m, 0.08m, 0.1m, 0.1m), 0.9m)]
                    : [],
                [$"narration-{index + 1}"]);
        }).ToArray();
        var narration = scenes.Select((scene, index) => new NarrationSegment(
            $"narration-{index + 1}", scene.StartMs + 500, 1_000, $"Scene {index + 1}", "story.voiceover"))
            .ToArray();
        var master = new VideoProductionSpecification(
            "1.0",
            Guid.NewGuid(),
            Guid.NewGuid(),
            4,
            RequestedDuration.Hero60,
            VideoAspectRatio.Landscape16By9,
            new VideoOutputProfile(1_920, 1_080, 30, "h264", "aac", "yuv420p", 48_000, 2),
            new NormalizedRect(0.05m, 0.05m, 0.9m, 0.9m),
            [
                new FactBinding("property.address.full", "123 Main Street", FactSource.VerifiedProperty, "Address"),
                new FactBinding("story.feature", "A cinematic welcome", FactSource.PropertyStory, "HeroAngle"),
                new FactBinding("story.closingCta", "Contact the listing team.", FactSource.PropertyStory, "ClosingCta"),
                new FactBinding("story.voiceover", "Approved voiceover", FactSource.PropertyStory, "Voiceover"),
            ],
            new BrandKit("agent-logo", null, "Avery Agent", "555-0100", null, null, "#17324D", "#F4F0E8"),
            new GroundedText("Contact the listing team.", "story.closingCta"),
            new AudioPlan(
                narration,
                new MusicPlan("licensed-music", MusicMood.WarmCinematic, 0, 60_000, -18, 1_000, 1_000, -24)),
            scenes);
        return new Fixture(
            master,
            new CampaignDerivativeRequest(master, [new CampaignDerivativeMedia(mediaId, width, height)]),
            mediaId,
            generatedClipId,
            width,
            height);
    }

    private static void AssertViewportAspect(
        NormalizedRect viewport,
        int width,
        int height,
        decimal expected)
    {
        var actual = viewport.Width * width / (viewport.Height * height);
        Assert.InRange(actual, expected - 0.02m, expected + 0.02m);
    }

    private static void AssertInside(NormalizedRect box, NormalizedRect safeZone)
    {
        Assert.True(box.X >= safeZone.X);
        Assert.True(box.Y >= safeZone.Y);
        Assert.True(box.X + box.Width <= safeZone.X + safeZone.Width);
        Assert.True(box.Y + box.Height <= safeZone.Y + safeZone.Height);
    }

    private sealed record Fixture(
        VideoProductionSpecification Master,
        CampaignDerivativeRequest Request,
        Guid MediaId,
        Guid GeneratedClipId,
        int MediaWidth,
        int MediaHeight);
}
