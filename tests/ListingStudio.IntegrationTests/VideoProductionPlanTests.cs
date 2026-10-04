using ListingStudio.Application.Authentication;
using System.Text.Json;
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

public sealed class VideoProductionPlanTests(PostgreSqlWebApplicationFixture fixture)
    : IClassFixture<PostgreSqlWebApplicationFixture>
{
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Z+VAAAAAASUVORK5CYII=");

    [Fact]
    public async Task StoresValidatedHeroPlanReusesFingerprintVersionsChangesAndEnforcesTenantScope()
    {
        var owner = await CreateOwnerAndPropertyAsync("video-owner");
        var outsider = await CreateOwnerAndPropertyAsync("video-outsider");
        await UploadAndAnalyzeAsync(owner);
        fixture.Factory.StoryGenerator.Enqueue(SafeStory);
        await GenerateStoryAsync(owner);
        var callsBefore = fixture.Factory.VideoDirector.CallCount;
        fixture.Factory.VideoDirector.Enqueue(CreateEditorialPlan);

        VideoProductionPlanResult generated;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var plans = scope.ServiceProvider.GetRequiredService<IVideoProductionPlanService>();
            generated = (await plans.GenerateAsync(
                owner.UserId,
                owner.PropertyId,
                RequestedDuration.Hero60,
                VideoAspectRatio.Landscape16By9))!;
            Assert.False(generated.Reused);
            Assert.Equal(1, generated.Version);
            Assert.Equal(60_000, generated.Specification.Scenes.Sum(scene => scene.DurationMs));
            Assert.Equal("fake-video-director-v1", generated.DirectorVersion);

            var cached = (await plans.GenerateAsync(
                owner.UserId,
                owner.PropertyId,
                RequestedDuration.Hero60,
                VideoAspectRatio.Landscape16By9))!;
            Assert.True(cached.Reused);
            Assert.Equal(generated.Id, cached.Id);

            Assert.Null(await plans.GetLatestAsync(
                outsider.UserId,
                owner.PropertyId,
                RequestedDuration.Hero60,
                VideoAspectRatio.Landscape16By9));
            Assert.Null(await plans.GenerateAsync(
                outsider.UserId,
                owner.PropertyId,
                RequestedDuration.Hero60,
                VideoAspectRatio.Landscape16By9));
        }

        Assert.Equal(callsBefore + 1, fixture.Factory.VideoDirector.CallCount);
        var request = Assert.IsType<VideoDirectionRequest>(fixture.Factory.VideoDirector.LastRequest);
        Assert.Equal(owner.PropertyId, request.PropertyId);
        Assert.Single(request.Media);
        Assert.Contains(request.FactBindings, binding => binding.Key == "story.closingCta");

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var persisted = await dbContext.VideoProductionPlans
                .AsNoTracking()
                .SingleAsync(plan => plan.Id == generated.Id);
            Assert.Equal(owner.OrganizationId, persisted.OrganizationId);
            Assert.Equal("1.0", persisted.SchemaVersion);
            using var specificationJson = JsonDocument.Parse(persisted.SpecificationJson);
            Assert.Equal(60, specificationJson.RootElement.GetProperty("requestedDurationSeconds").GetInt32());
            Assert.Equal("16:9", specificationJson.RootElement.GetProperty("aspectRatio").GetString());
        }

        await UpdateDescriptionAsync(owner, "A comfortable home with a new verified description.");
        fixture.Factory.StoryGenerator.Enqueue(SafeStory with
        {
            PropertyNarrative = "This 3-bedroom, 2.5-bath home offers 2,100 square feet with a new description.",
        });
        await GenerateStoryAsync(owner);
        fixture.Factory.VideoDirector.Enqueue(CreateEditorialPlan);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var plans = scope.ServiceProvider.GetRequiredService<IVideoProductionPlanService>();
            var second = (await plans.GenerateAsync(
                owner.UserId,
                owner.PropertyId,
                RequestedDuration.Hero60,
                VideoAspectRatio.Landscape16By9))!;
            Assert.Equal(2, second.Version);
            Assert.False(second.Reused);
            Assert.NotEqual(generated.PropertyStoryId, second.PropertyStoryId);
        }
    }

    [Fact]
    public async Task RejectsInvalidDirectorOutputWithoutPersisting()
    {
        var owner = await CreateOwnerAndPropertyAsync("invalid-video-owner");
        await UploadAndAnalyzeAsync(owner);
        fixture.Factory.StoryGenerator.Enqueue(SafeStory);
        await GenerateStoryAsync(owner);
        fixture.Factory.VideoDirector.Enqueue(request =>
        {
            var valid = CreateEditorialPlan(request);
            var scene = valid.Scenes[0];
            return valid with
            {
                Scenes = [scene with { DurationMs = scene.DurationMs - 1 }],
            };
        });

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var plans = scope.ServiceProvider.GetRequiredService<IVideoProductionPlanService>();
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => plans.GenerateAsync(
            owner.UserId,
            owner.PropertyId,
            RequestedDuration.Hero60,
            VideoAspectRatio.Landscape16By9));
        Assert.Contains("failed validation", exception.Message, StringComparison.Ordinal);

        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await dbContext.VideoProductionPlans.AnyAsync(plan => plan.PropertyId == owner.PropertyId));
    }

    [Fact]
    public async Task RequiresCompletedAnalysisAndGroundedStoryBeforeCallingDirector()
    {
        var callsBefore = fixture.Factory.VideoDirector.CallCount;
        var pending = await CreateOwnerAndPropertyAsync("pending-video-owner");
        await UploadAsync(pending);

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var plans = scope.ServiceProvider.GetRequiredService<IVideoProductionPlanService>();
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => plans.GenerateAsync(
                pending.UserId,
                pending.PropertyId,
                RequestedDuration.Hero60,
                VideoAspectRatio.Landscape16By9));
            Assert.Contains("completed analysis", exception.Message, StringComparison.Ordinal);
        }

        await AnalyzeNextAsync();
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var plans = scope.ServiceProvider.GetRequiredService<IVideoProductionPlanService>();
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => plans.GenerateAsync(
                pending.UserId,
                pending.PropertyId,
                RequestedDuration.Hero60,
                VideoAspectRatio.Landscape16By9));
            Assert.Contains("grounded property story", exception.Message, StringComparison.Ordinal);
        }

        Assert.Equal(callsBefore, fixture.Factory.VideoDirector.CallCount);
    }

    private static DirectedEditorialPlan CreateEditorialPlan(VideoDirectionRequest request)
    {
        var narrationBinding = request.FactBindings.Single(binding => binding.Key == "story.voiceover");
        var cta = request.CallToAction;
        var media = request.Media[0];
        var viewportHeight = 9m * media.Width / (16m * media.Height);
        var viewport = viewportHeight <= 1
            ? new NormalizedRect(0, 0, 1, viewportHeight)
            : new NormalizedRect(0, 0, 16m * media.Height / (9m * media.Width), 1);
        var duration = (int)request.RequestedDuration * 1_000;
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
                    duration,
                    new VisualSource(VisualSourceKind.PropertyMedia, media.MediaId, null, null, null),
                    new TransitionPlan(TransitionKind.Cut, 0),
                    new MotionPlan(MotionKind.None, viewport, viewport, MotionEasing.Linear),
                    [
                        new TextOverlay(
                            "closing-cta", cta.Text, cta.GroundingKey, duration - 5_000, 4_000,
                            OverlayAnchor.BottomCenter, new NormalizedRect(0.15m, 0.75m, 0.7m, 0.1m),
                            TextOverlayStyle.ClosingCta),
                    ],
                    [],
                    [narration.Id]),
            ]);
    }

    private async Task GenerateStoryAsync(OwnerProperty owner)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider
            .GetRequiredService<IPropertyStoryService>()
            .GenerateAsync(owner.UserId, owner.PropertyId);
        Assert.NotNull(result);
    }

    private async Task UploadAndAnalyzeAsync(OwnerProperty owner)
    {
        await UploadAsync(owner);

        await AnalyzeNextAsync();
    }

    private async Task AnalyzeNextAsync()
    {
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

    private async Task UploadAsync(OwnerProperty owner)
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

    private async Task UpdateDescriptionAsync(OwnerProperty owner, string description)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var properties = scope.ServiceProvider.GetRequiredService<IPropertyService>();
        Assert.True(await properties.UpdateAsync(
            owner.UserId,
            owner.PropertyId,
            ValidProperty with { Description = description }));
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
