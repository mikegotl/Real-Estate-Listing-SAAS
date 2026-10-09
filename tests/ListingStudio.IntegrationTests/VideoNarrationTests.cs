using ListingStudio.Application.Authentication;
using ListingStudio.Application.Audio;
using ListingStudio.Application.Properties;
using ListingStudio.Application.Stories;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Properties;
using ListingStudio.Domain.Stories;
using ListingStudio.Domain.Videos;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed class VideoNarrationTests(PostgreSqlWebApplicationFixture fixture)
    : IClassFixture<PostgreSqlWebApplicationFixture>
{
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Z+VAAAAAASUVORK5CYII=");

    [Fact]
    public async Task GeneratesStoresReusesAndVersionsNarrationWithTenantScopedAudio()
    {
        fixture.Factory.VoiceProvider.SetGenerationVersion("fake-voice-cache-v1");
        var owner = await CreateOwnerAndPropertyAsync("narration-owner");
        var outsider = await CreateOwnerAndPropertyAsync("narration-outsider");
        var plan = await CreateProductionPlanAsync(owner);
        var callsBefore = fixture.Factory.VoiceProvider.CallCount;
        fixture.Factory.VoiceProvider.Enqueue(CreateVoiceResult);

        VideoNarrationResult generated;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var narrations = scope.ServiceProvider.GetRequiredService<IVideoNarrationService>();
            generated = (await narrations.GenerateAsync(owner.UserId, plan.Id))!;
            Assert.False(generated.Reused);
            Assert.Equal(1, generated.Version);
            Assert.Equal(1_000, generated.DurationMs);
            Assert.Equal("narration-1", Assert.Single(generated.Timing!.Segments).SegmentId);

            var cached = (await narrations.GenerateAsync(owner.UserId, plan.Id))!;
            Assert.True(cached.Reused);
            Assert.Equal(generated.Id, cached.Id);

            Assert.Null(await narrations.GetLatestAsync(outsider.UserId, plan.Id));
            Assert.Null(await narrations.GenerateAsync(outsider.UserId, plan.Id));
            Assert.Null(await narrations.OpenAudioAsync(outsider.UserId, generated.Id));

            var content = await narrations.OpenAudioAsync(owner.UserId, generated.Id);
            Assert.NotNull(content);
            await using var stream = content.Content;
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, buffer.ToArray());
            Assert.Equal("audio/mpeg", content.ContentType);
        }

        Assert.Equal(callsBefore + 1, fixture.Factory.VoiceProvider.CallCount);
        var request = Assert.IsType<VoiceGenerationRequest>(fixture.Factory.VoiceProvider.LastRequest);
        Assert.Equal("Welcome to 123 Main Street. Explore a comfortable home in Raleigh.",
            Assert.Single(request.Segments).Text);

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var persisted = await dbContext.VideoNarrations.AsNoTracking().SingleAsync(item => item.Id == generated.Id);
            Assert.Equal(owner.OrganizationId, persisted.OrganizationId);
            Assert.Equal(owner.PropertyId, persisted.PropertyId);
            Assert.Equal(plan.Id, persisted.VideoProductionPlanId);
            Assert.NotNull(persisted.TimingJson);
            Assert.DoesNotContain("test-api-key", persisted.TimingJson, StringComparison.Ordinal);
        }

        fixture.Factory.VoiceProvider.SetGenerationVersion("fake-voice-cache-v2");
        fixture.Factory.VoiceProvider.Enqueue(CreateVoiceResult);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var narrations = scope.ServiceProvider.GetRequiredService<IVideoNarrationService>();
            var second = (await narrations.GenerateAsync(owner.UserId, plan.Id))!;
            Assert.Equal(2, second.Version);
            Assert.False(second.Reused);
            Assert.NotEqual(generated.Id, second.Id);
        }
    }

    [Fact]
    public async Task RejectsNarrationThatRunsPastTheVideoWithoutStoringAudioMetadata()
    {
        fixture.Factory.VoiceProvider.SetGenerationVersion("fake-voice-invalid-v1");
        var owner = await CreateOwnerAndPropertyAsync("long-narration-owner");
        var plan = await CreateProductionPlanAsync(owner);
        fixture.Factory.VoiceProvider.Enqueue(request =>
        {
            var segment = Assert.Single(request.Segments);
            var spokenMs = 61_000;
            return new VoiceGenerationResult(
                [1, 2, 3],
                "audio/mpeg",
                ".mp3",
                spokenMs,
                new VoiceTimingMetadata(
                    [new VoiceCharacterTiming("A", 0, spokenMs)],
                    [new VoiceSegmentTiming(segment.Id, 0, spokenMs)]));
        });

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var narrations = scope.ServiceProvider.GetRequiredService<IVideoNarrationService>();
        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => narrations.GenerateAsync(owner.UserId, plan.Id));
        Assert.Contains("runs past the end of the video", exception.Message, StringComparison.Ordinal);

        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await dbContext.VideoNarrations.AnyAsync(item => item.VideoProductionPlanId == plan.Id));
    }

    private async Task<VideoProductionPlanResult> CreateProductionPlanAsync(OwnerProperty owner)
    {
        await UploadAndAnalyzeAsync(owner);
        fixture.Factory.StoryGenerator.Enqueue(SafeStory);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var story = await scope.ServiceProvider.GetRequiredService<IPropertyStoryService>()
                .GenerateAsync(owner.UserId, owner.PropertyId);
            Assert.NotNull(story);
        }

        fixture.Factory.VideoDirector.Enqueue(CreateEditorialPlan);
        await using var planScope = fixture.Factory.Services.CreateAsyncScope();
        return (await planScope.ServiceProvider.GetRequiredService<IVideoProductionPlanService>().GenerateAsync(
            owner.UserId,
            owner.PropertyId,
            RequestedDuration.Hero60,
            VideoAspectRatio.Landscape16By9))!;
    }

    private static DirectedEditorialPlan CreateEditorialPlan(VideoDirectionRequest request)
    {
        var narrationBinding = request.FactBindings.Single(binding => binding.Key == "story.voiceover");
        var media = request.Media[0];
        var viewportHeight = 9m * media.Width / (16m * media.Height);
        var viewport = viewportHeight <= 1
            ? new NormalizedRect(0, 0, 1, viewportHeight)
            : new NormalizedRect(0, 0, 16m * media.Height / (9m * media.Width), 1);
        var narration = new NarrationSegment(
            "narration-1", 500, 3_000, narrationBinding.Value, narrationBinding.Key);
        return new DirectedEditorialPlan(
            new AudioPlan(
                [narration],
                new MusicPlan(null, MusicMood.None, 0, 0, 0, 0, 0, 0)),
            [
                new VideoScene(
                    1,
                    0,
                    60_000,
                    new VisualSource(VisualSourceKind.PropertyMedia, media.MediaId, null, null, null),
                    new TransitionPlan(TransitionKind.Cut, 0),
                    new MotionPlan(MotionKind.None, viewport, viewport, MotionEasing.Linear),
                    [
                        new TextOverlay(
                            "closing-cta", request.CallToAction.Text, request.CallToAction.GroundingKey,
                            55_000, 4_000, OverlayAnchor.BottomCenter,
                            new NormalizedRect(0.15m, 0.75m, 0.7m, 0.1m),
                            TextOverlayStyle.ClosingCta),
                    ],
                    [],
                    [narration.Id]),
            ]);
    }

    private static VoiceGenerationResult CreateVoiceResult(VoiceGenerationRequest request)
    {
        var segment = Assert.Single(request.Segments);
        return new VoiceGenerationResult(
            [1, 2, 3, 4],
            "audio/mpeg",
            ".mp3",
            1_000,
            new VoiceTimingMetadata(
                [new VoiceCharacterTiming("W", 0, 1_000)],
                [new VoiceSegmentTiming(segment.Id, 0, 1_000)]));
    }

    private async Task UploadAndAnalyzeAsync(OwnerProperty owner)
    {
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var media = scope.ServiceProvider.GetRequiredService<IPropertyMediaService>();
            await using var content = new MemoryStream(OnePixelPng);
            await media.UploadAsync(
                owner.UserId,
                owner.PropertyId,
                new PropertyMediaUpload("front.png", "image/png", content.Length, content));
        }

        fixture.Factory.MediaAnalyzer.Enqueue(new PropertyMediaAnalysis(
            PropertyMediaCategory.FrontExterior,
            "Exterior",
            91,
            96,
            true,
            false,
            false,
            [],
            "Front exterior of a detached home.",
            0));
        await using var analysisScope = fixture.Factory.Services.CreateAsyncScope();
        var result = await analysisScope.ServiceProvider
            .GetRequiredService<IPropertyMediaAnalysisProcessor>()
            .AnalyzeNextAsync();
        Assert.True(result!.Succeeded);
    }

    private async Task<OwnerProperty> CreateOwnerAndPropertyAsync(string prefix)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var registration = scope.ServiceProvider.GetRequiredService<IAccountRegistrationService>();
        var properties = scope.ServiceProvider.GetRequiredService<IPropertyService>();
        var result = await registration.RegisterAsync(new RegisterAccountCommand(
            $"{prefix}-{Guid.NewGuid():N}@example.com",
            "Password123",
            $"{prefix} Realty {Guid.NewGuid():N}"));
        Assert.True(result.Succeeded, string.Join(", ", result.Errors));
        var propertyId = await properties.CreateAsync(result.UserId!, ValidProperty);
        return new OwnerProperty(result.UserId!, result.OrganizationId!.Value, propertyId);
    }

    private static PropertyInput ValidProperty => new(
        "123 Main Street", null, "Raleigh", "NC", "27601", 450_000m, 3, 2.5m, 2_100,
        0.25m, 1998, PropertyType.SingleFamily, "A comfortable home.", ListingStatus.Active);

    private static PropertyStoryContent SafeStory => new(
        "Welcome to 123 Main Street",
        "A comfortable home in Raleigh.",
        "This 3-bedroom, 2.5-bath home offers 2,100 square feet and was built in 1998.",
        ["Listed at $450,000", "A 0.25-acre lot"],
        "Welcome to 123 Main Street. Explore a comfortable home in Raleigh.",
        "Contact the listing team to learn more.",
        "Explore 123 Main Street, listed at $450,000.",
        "Discover 123 Main Street.");

    private sealed record OwnerProperty(string UserId, Guid OrganizationId, Guid PropertyId);
}
