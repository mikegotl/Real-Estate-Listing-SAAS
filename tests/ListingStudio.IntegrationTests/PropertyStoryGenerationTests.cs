using ListingStudio.Application.Authentication;
using ListingStudio.Application.Properties;
using ListingStudio.Application.Stories;
using ListingStudio.Domain.Properties;
using ListingStudio.Domain.Stories;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed class PropertyStoryGenerationTests(PostgreSqlWebApplicationFixture fixture)
    : IClassFixture<PostgreSqlWebApplicationFixture>
{
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Z+VAAAAAASUVORK5CYII=");

    [Fact]
    public async Task GeneratesGroundedVersionedStoryCachesIdenticalInputAndEnforcesTenantScope()
    {
        var owner = await CreateOwnerAndPropertyAsync("story-owner");
        var outsider = await CreateOwnerAndPropertyAsync("story-outsider");
        await UploadAndAnalyzeAsync(owner);
        fixture.Factory.StoryGenerator.Enqueue(SafeStory);

        PropertyStoryResult generated;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var stories = scope.ServiceProvider.GetRequiredService<IPropertyStoryService>();
            generated = (await stories.GenerateAsync(owner.UserId, owner.PropertyId))!;
            Assert.NotNull(generated);
            Assert.False(generated.Reused);
            Assert.Equal(1, generated.Version);
            Assert.Equal("fake-story-v1", generated.GenerationVersion);
            Assert.Equal("Welcome to 123 Main Street", generated.Content.CampaignTitle);

            var cached = (await stories.GenerateAsync(owner.UserId, owner.PropertyId))!;
            Assert.True(cached.Reused);
            Assert.Equal(generated.Id, cached.Id);

            Assert.Null(await stories.GetLatestAsync(outsider.UserId, owner.PropertyId));
            Assert.Null(await stories.GenerateAsync(outsider.UserId, owner.PropertyId));
        }

        Assert.Equal(1, fixture.Factory.StoryGenerator.CallCount);
        var request = Assert.IsType<PropertyStoryGenerationRequest>(fixture.Factory.StoryGenerator.LastRequest);
        Assert.Equal("123 Main Street", request.VerifiedProperty.Address1);
        Assert.Equal(3, request.VerifiedProperty.Bedrooms);
        Assert.Single(request.MediaObservations);
        Assert.Equal(PropertyMediaCategory.FrontExterior, request.MediaObservations[0].Category);
        Assert.StartsWith("story-owner Realty", request.Branding.OrganizationName, StringComparison.Ordinal);

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var persisted = await dbContext.PropertyStories.AsNoTracking().SingleAsync();
            Assert.Equal(owner.OrganizationId, persisted.OrganizationId);
            Assert.Equal(owner.PropertyId, persisted.PropertyId);
            Assert.Equal(SafeStory.Highlights, persisted.Highlights);
        }

        await UpdateDescriptionAsync(owner, "A comfortable home with flexible living space.");
        fixture.Factory.StoryGenerator.Enqueue(SafeStory with
        {
            PropertyNarrative = "This 3-bedroom, 2.5-bath home offers 2,100 square feet and flexible living space.",
        });
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var stories = scope.ServiceProvider.GetRequiredService<IPropertyStoryService>();
            var second = (await stories.GenerateAsync(owner.UserId, owner.PropertyId))!;
            Assert.Equal(2, second.Version);
            Assert.False(second.Reused);
        }

        await UpdateDescriptionAsync(owner, "A comfortable home with an updated description.");
        fixture.Factory.StoryGenerator.Enqueue(SafeStory with
        {
            PropertyNarrative = "This 4-bedroom home is near a top school.",
        });
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var stories = scope.ServiceProvider.GetRequiredService<IPropertyStoryService>();
            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => stories.GenerateAsync(owner.UserId, owner.PropertyId));
            Assert.Contains("unverified property information", exception.Message, StringComparison.Ordinal);
            Assert.Contains("\"School\" is not verified", exception.Message, StringComparison.Ordinal);
            Assert.Contains("add that fact to the Description", exception.Message, StringComparison.Ordinal);
        }

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Equal(2, await dbContext.PropertyStories.CountAsync());
        }
    }

    [Fact]
    public async Task RequiresCompletedAnalysisBeforeCallingGenerator()
    {
        var callsBefore = fixture.Factory.StoryGenerator.CallCount;
        var owner = await CreateOwnerAndPropertyAsync("pending-story-owner");
        await UploadAsync(owner);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var stories = scope.ServiceProvider.GetRequiredService<IPropertyStoryService>();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => stories.GenerateAsync(owner.UserId, owner.PropertyId));

        Assert.Contains("completed analysis", exception.Message, StringComparison.Ordinal);
        Assert.Equal(callsBefore, fixture.Factory.StoryGenerator.CallCount);
    }

    private async Task UploadAndAnalyzeAsync(OwnerProperty owner)
    {
        await UploadAsync(owner);
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

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider
            .GetRequiredService<IPropertyMediaAnalysisProcessor>()
            .AnalyzeNextAsync();
        Assert.True(result!.Succeeded);
    }

    private async Task UploadAsync(OwnerProperty owner)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var media = scope.ServiceProvider.GetRequiredService<IPropertyMediaService>();
        await using var content = new MemoryStream(OnePixelPng);
        await media.UploadAsync(
            owner.UserId,
            owner.PropertyId,
            new PropertyMediaUpload("front.png", "image/png", content.Length, content));
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
        "123 Main Street",
        null,
        "Raleigh",
        "NC",
        "27601",
        450_000m,
        3,
        2.5m,
        2_100,
        0.25m,
        1998,
        PropertyType.SingleFamily,
        "A comfortable home.",
        ListingStatus.Active);

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
