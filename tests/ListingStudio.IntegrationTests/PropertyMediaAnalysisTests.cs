using ListingStudio.Application.Authentication;
using ListingStudio.Application.Properties;
using ListingStudio.Domain.Properties;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed class PropertyMediaAnalysisTests(PostgreSqlWebApplicationFixture fixture)
    : IClassFixture<PostgreSqlWebApplicationFixture>
{
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Z+VAAAAAASUVORK5CYII=");

    [Fact]
    public async Task BackgroundProcessorPersistsEveryResultAndSupportsAutomaticAndExplicitRetry()
    {
        var owner = await CreateOwnerAndPropertyAsync("analysis-owner");
        var outsider = await CreateOwnerAndPropertyAsync("analysis-outsider");
        var mediaIds = await UploadAsync(owner, "kitchen.png", "exterior.png");
        fixture.Factory.MediaAnalyzer.Enqueue(Analysis(PropertyMediaCategory.Kitchen, "Kitchen", 90, 84, isInterior: true));
        fixture.Factory.MediaAnalyzer.Enqueue(Analysis(PropertyMediaCategory.FrontExterior, "Exterior", 94, 98, isExterior: true));

        var firstRun = await ProcessNextAsync();
        var secondRun = await ProcessNextAsync();

        Assert.True(firstRun!.Succeeded);
        Assert.True(secondRun!.Succeeded);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var mediaService = scope.ServiceProvider.GetRequiredService<IPropertyMediaService>();
            var items = await mediaService.ListAsync(owner.UserId, owner.PropertyId);
            Assert.Equal(2, items.Count);
            Assert.Equal(mediaIds, items.Select(item => item.Id));
            Assert.All(items, item => Assert.Equal(PropertyMediaAnalysisStatus.Completed, item.AnalysisStatus));
            var kitchen = Assert.Single(items, item => item.Analysis?.Category == PropertyMediaCategory.Kitchen);
            Assert.Equal("Kitchen", kitchen.Analysis!.RoomType);
            Assert.Equal(90, kitchen.Analysis.QualityScore);
            Assert.Equal(84, kitchen.Analysis.HeroScore);
            Assert.True(kitchen.Analysis.IsInterior);
            Assert.False(kitchen.Analysis.IsExterior);
            Assert.False(kitchen.Analysis.ContainsPeople);
            Assert.Empty(kitchen.Analysis.PotentialProblems);
            Assert.Equal("Visible kitchen details.", kitchen.Analysis.Description);
            Assert.Equal(0, kitchen.Analysis.SuggestedDisplayOrder);

            var exterior = Assert.Single(
                items,
                item => item.Analysis?.Category == PropertyMediaCategory.FrontExterior);
            Assert.True(exterior.Analysis!.IsExterior);
            Assert.Equal(98, exterior.Analysis.HeroScore);
        }

        var failedMediaId = (await UploadAsync(owner, "retry.png")).Single();
        fixture.Factory.MediaAnalyzer.Enqueue(new HttpRequestException("Temporary provider failure."));
        var failedRun = await ProcessNextAsync();
        Assert.Equal(failedMediaId, failedRun!.MediaId);
        Assert.False(failedRun.Succeeded);
        Assert.True(failedRun.WillRetry);
        Assert.Null(await ProcessNextAsync());

        fixture.Factory.TimeProvider.Advance(TimeSpan.FromMinutes(1));
        fixture.Factory.MediaAnalyzer.Enqueue(Analysis(PropertyMediaCategory.Patio, "Patio", 82, 74, isExterior: true));
        var automaticRetry = await ProcessNextAsync();
        Assert.True(automaticRetry!.Succeeded);
        Assert.Equal(2, automaticRetry.AttemptNumber);

        var exhaustedMediaId = (await UploadAsync(owner, "exhausted.png")).Single();
        fixture.Factory.MediaAnalyzer.Enqueue(new HttpRequestException("Temporary provider failure."));
        var exhaustedFirst = await ProcessNextAsync();
        Assert.True(exhaustedFirst!.WillRetry);
        fixture.Factory.TimeProvider.Advance(TimeSpan.FromMinutes(1));
        fixture.Factory.MediaAnalyzer.Enqueue(new HttpRequestException("Temporary provider failure."));
        var exhaustedSecond = await ProcessNextAsync();
        Assert.True(exhaustedSecond!.WillRetry);
        fixture.Factory.TimeProvider.Advance(TimeSpan.FromMinutes(2));
        fixture.Factory.MediaAnalyzer.Enqueue(new HttpRequestException("Temporary provider failure."));
        var exhaustedThird = await ProcessNextAsync();
        Assert.Equal(exhaustedMediaId, exhaustedThird!.MediaId);
        Assert.False(exhaustedThird.WillRetry);
        Assert.Equal(3, exhaustedThird.AttemptNumber);

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var mediaService = scope.ServiceProvider.GetRequiredService<IPropertyMediaService>();
            Assert.False(await mediaService.RetryAnalysisAsync(outsider.UserId, owner.PropertyId, exhaustedMediaId));
            Assert.True(await mediaService.RetryAnalysisAsync(owner.UserId, owner.PropertyId, exhaustedMediaId));
        }

        fixture.Factory.MediaAnalyzer.Enqueue(Analysis(PropertyMediaCategory.Backyard, "Backyard", 78, 70, isExterior: true));
        var retryRun = await ProcessNextAsync();
        Assert.True(retryRun!.Succeeded);
        Assert.Equal(1, retryRun.AttemptNumber);

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var mediaService = scope.ServiceProvider.GetRequiredService<IPropertyMediaService>();
            var retried = Assert.Single(
                await mediaService.ListAsync(owner.UserId, owner.PropertyId),
                item => item.Id == exhaustedMediaId);
            Assert.Equal(PropertyMediaAnalysisStatus.Completed, retried.AnalysisStatus);
            Assert.Equal(PropertyMediaCategory.Backyard, retried.Analysis!.Category);
            Assert.Equal(1, retried.AnalysisAttemptCount);
        }
    }

    private async Task<PropertyMediaAnalysisRunResult?> ProcessNextAsync()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IPropertyMediaAnalysisProcessor>()
            .AnalyzeNextAsync();
    }

    private async Task<Guid[]> UploadAsync(
        (string UserId, Guid PropertyId) owner,
        params string[] filenames)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var mediaService = scope.ServiceProvider.GetRequiredService<IPropertyMediaService>();
        var ids = new List<Guid>();
        foreach (var filename in filenames)
        {
            await using var content = new MemoryStream(OnePixelPng);
            ids.Add(await mediaService.UploadAsync(
                owner.UserId,
                owner.PropertyId,
                new PropertyMediaUpload(filename, "image/png", content.Length, content)));
        }

        return [.. ids];
    }

    private async Task<(string UserId, Guid PropertyId)> CreateOwnerAndPropertyAsync(string prefix)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var registration = scope.ServiceProvider.GetRequiredService<IAccountRegistrationService>();
        var properties = scope.ServiceProvider.GetRequiredService<IPropertyService>();
        var registrationResult = await registration.RegisterAsync(new RegisterAccountCommand(
            $"{prefix}-{Guid.NewGuid():N}@example.com",
            "Password123",
            $"{prefix} Realty {Guid.NewGuid():N}"));
        Assert.True(registrationResult.Succeeded, string.Join(", ", registrationResult.Errors));
        var propertyId = await properties.CreateAsync(registrationResult.UserId!, ValidProperty);
        return (registrationResult.UserId!, propertyId);
    }

    private static PropertyMediaAnalysis Analysis(
        PropertyMediaCategory category,
        string roomType,
        int quality,
        int hero,
        bool isExterior = false,
        bool isInterior = false) => new(
            category,
            roomType,
            quality,
            hero,
            isExterior,
            isInterior,
            false,
            [],
            $"Visible {roomType.ToLowerInvariant()} details.",
            0);

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
        ListingStatus.Draft);
}
