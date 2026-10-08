using System.Buffers.Binary;
using ListingStudio.Application.Authentication;
using ListingStudio.Application.Properties;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Properties;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed class PropertyMediaManagementTests(PostgreSqlWebApplicationFixture fixture)
    : IClassFixture<PostgreSqlWebApplicationFixture>
{
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Z+VAAAAAASUVORK5CYII=");

    [Fact]
    public async Task OwnerCanUploadFortyReorderReadAndDeletePhotos()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (userId, propertyId) = await CreateOwnerAndPropertyAsync(scope.ServiceProvider, "forty");
        var mediaService = scope.ServiceProvider.GetRequiredService<IPropertyMediaService>();

        for (var index = 0; index < 40; index++)
        {
            await using var content = new MemoryStream(OnePixelPng);
            await mediaService.UploadAsync(
                userId,
                propertyId,
                new PropertyMediaUpload($"photo-{index:00}.png", "image/png", content.Length, content));
        }

        var uploaded = await mediaService.ListAsync(userId, propertyId);
        Assert.Equal(40, uploaded.Count);
        Assert.All(uploaded, item =>
        {
            Assert.Equal(1, item.Width);
            Assert.Equal(1, item.Height);
            Assert.Equal(PropertyMediaAnalysisStatus.Pending, item.AnalysisStatus);
        });

        var reversed = uploaded.Select(item => item.Id).Reverse().ToArray();
        Assert.True(await mediaService.ReorderAsync(userId, propertyId, reversed));
        var reordered = await mediaService.ListAsync(userId, propertyId);
        Assert.Equal(reversed, reordered.Select(item => item.Id));

        var first = await mediaService.OpenReadAsync(userId, reordered[0].Id);
        Assert.NotNull(first);
        await using (first.Content)
        {
            using var copy = new MemoryStream();
            await first.Content.CopyToAsync(copy);
            Assert.Equal(OnePixelPng, copy.ToArray());
        }

        Assert.True(await mediaService.DeleteAsync(userId, propertyId, reordered[0].Id));
        Assert.Equal(39, (await mediaService.ListAsync(userId, propertyId)).Count);
        Assert.Null(await mediaService.OpenReadAsync(userId, reordered[0].Id));
    }

    [Fact]
    public async Task PropertyListSummarizesPhotosCoverAndLatestCampaign()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (userId, propertyId) = await CreateOwnerAndPropertyAsync(scope.ServiceProvider, "summary");
        var mediaService = scope.ServiceProvider.GetRequiredService<IPropertyMediaService>();
        var properties = scope.ServiceProvider.GetRequiredService<IPropertyService>();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var empty = Assert.Single(await properties.ListAsync(userId), item => item.Id == propertyId);
        Assert.Equal(0, empty.PhotoCount);
        Assert.Null(empty.CoverMediaId);
        Assert.Null(empty.LatestCampaignStatus);

        for (var index = 0; index < 2; index++)
        {
            await using var content = new MemoryStream(OnePixelPng);
            await mediaService.UploadAsync(
                userId,
                propertyId,
                new PropertyMediaUpload($"photo-{index}.png", "image/png", content.Length, content));
        }

        var uploaded = await mediaService.ListAsync(userId, propertyId);
        Assert.True(await mediaService.ReorderAsync(userId, propertyId, [uploaded[1].Id, uploaded[0].Id]));
        var failed = await dbContext.PropertyMedia.SingleAsync(media => media.Id == uploaded[0].Id);
        failed.BeginAnalysis(DateTimeOffset.UtcNow);
        failed.FailAnalysis("Vision request failed.", nextAttemptAtUtc: null);
        var property = await dbContext.Properties.SingleAsync(candidate => candidate.Id == propertyId);
        dbContext.CampaignGenerationJobs.Add(CampaignGenerationJob.Create(
            property.OrganizationId, propertyId, userId, "fingerprint-summary", DateTimeOffset.UtcNow));
        await dbContext.SaveChangesAsync();

        var summary = Assert.Single(await properties.ListAsync(userId), item => item.Id == propertyId);
        Assert.Equal(2, summary.PhotoCount);
        Assert.Equal(1, summary.FailedAnalysisCount);
        Assert.Equal(uploaded[1].Id, summary.CoverMediaId);
        Assert.Equal(CampaignGenerationStatus.Queued, summary.LatestCampaignStatus);
        Assert.Equal(3, summary.Bedrooms);
        Assert.Equal(2_100, summary.SquareFeet);
    }

    [Fact]
    public async Task UploadValidatesTypeContentAndLimit()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (userId, propertyId) = await CreateOwnerAndPropertyAsync(scope.ServiceProvider, "validation");
        var mediaService = scope.ServiceProvider.GetRequiredService<IPropertyMediaService>();

        await using var wrongMime = new MemoryStream(OnePixelPng);
        await Assert.ThrowsAsync<InvalidDataException>(() => mediaService.UploadAsync(
            userId,
            propertyId,
            new PropertyMediaUpload("photo.png", "image/jpeg", wrongMime.Length, wrongMime)));

        await using var fakeImage = new MemoryStream("not an image"u8.ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => mediaService.UploadAsync(
            userId,
            propertyId,
            new PropertyMediaUpload("photo.png", "image/png", fakeImage.Length, fakeImage)));

        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var property = await dbContext.Properties.FindAsync(propertyId);
        Assert.NotNull(property);
        for (var index = 0; index < IPropertyMediaService.MaximumMediaPerProperty; index++)
        {
            dbContext.PropertyMedia.Add(PropertyMedia.Create(
                property.OrganizationId,
                propertyId,
                $"seed/{Guid.NewGuid():N}.png",
                $"seed-{index}.png",
                "image/png",
                OnePixelPng.Length,
                1,
                1,
                index));
        }

        await dbContext.SaveChangesAsync();
        await using var overLimit = new MemoryStream(OnePixelPng);
        await Assert.ThrowsAsync<InvalidOperationException>(() => mediaService.UploadAsync(
            userId,
            propertyId,
            new PropertyMediaUpload("extra.png", "image/png", overLimit.Length, overLimit)));
    }

    [Fact]
    public async Task UploadRejectsOversizedAndDeceptiveStreamsWithoutReadingPastDeclaredSize()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (userId, propertyId) = await CreateOwnerAndPropertyAsync(scope.ServiceProvider, "bounded-upload");
        var mediaService = scope.ServiceProvider.GetRequiredService<IPropertyMediaService>();

        await using var oversized = new MemoryStream(OnePixelPng);
        await Assert.ThrowsAsync<InvalidDataException>(() => mediaService.UploadAsync(
            userId,
            propertyId,
            new PropertyMediaUpload(
                "oversized.png",
                "image/png",
                IPropertyMediaService.MaximumFileSize + 1,
                oversized)));

        await using var deceptive = new CountingStream(new byte[1024]);
        await Assert.ThrowsAsync<InvalidDataException>(() => mediaService.UploadAsync(
            userId,
            propertyId,
            new PropertyMediaUpload("deceptive.png", "image/png", 8, deceptive)));

        Assert.Equal(9, deceptive.BytesRead);

        var excessiveDimensions = OnePixelPng.ToArray();
        BinaryPrimitives.WriteInt32BigEndian(excessiveDimensions.AsSpan(16, 4), 10_000);
        BinaryPrimitives.WriteInt32BigEndian(excessiveDimensions.AsSpan(20, 4), 10_000);
        await using var dimensionStream = new MemoryStream(excessiveDimensions);
        await Assert.ThrowsAsync<InvalidDataException>(() => mediaService.UploadAsync(
            userId,
            propertyId,
            new PropertyMediaUpload(
                "excessive-dimensions.png",
                "image/png",
                dimensionStream.Length,
                dimensionStream)));

        Assert.Empty(await mediaService.ListAsync(userId, propertyId));
    }

    [Fact]
    public async Task MediaAccessIsRestrictedToOwningOrganization()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var owner = await CreateOwnerAndPropertyAsync(scope.ServiceProvider, "media-owner");
        var outsider = await CreateOwnerAndPropertyAsync(scope.ServiceProvider, "media-outsider");
        var mediaService = scope.ServiceProvider.GetRequiredService<IPropertyMediaService>();
        await using var content = new MemoryStream(OnePixelPng);
        var mediaId = await mediaService.UploadAsync(
            owner.UserId,
            owner.PropertyId,
            new PropertyMediaUpload("private.png", "image/png", content.Length, content));

        Assert.Empty(await mediaService.ListAsync(outsider.UserId, owner.PropertyId));
        Assert.Null(await mediaService.OpenReadAsync(outsider.UserId, mediaId));
        Assert.False(await mediaService.DeleteAsync(outsider.UserId, owner.PropertyId, mediaId));
        Assert.False(await mediaService.ReorderAsync(outsider.UserId, owner.PropertyId, [mediaId]));
        await using var outsiderUpload = new MemoryStream(OnePixelPng);
        await Assert.ThrowsAsync<InvalidOperationException>(() => mediaService.UploadAsync(
            outsider.UserId,
            owner.PropertyId,
            new PropertyMediaUpload("blocked.png", "image/png", outsiderUpload.Length, outsiderUpload)));
    }

    [Fact]
    public async Task ArchivedPropertyMediaCanBeReadButNotChanged()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var owner = await CreateOwnerAndPropertyAsync(scope.ServiceProvider, "archived-media");
        var mediaService = scope.ServiceProvider.GetRequiredService<IPropertyMediaService>();
        var propertyService = scope.ServiceProvider.GetRequiredService<IPropertyService>();
        await using var content = new MemoryStream(OnePixelPng);
        var mediaId = await mediaService.UploadAsync(
            owner.UserId,
            owner.PropertyId,
            new PropertyMediaUpload("archived.png", "image/png", content.Length, content));

        Assert.True(await propertyService.ArchiveAsync(owner.UserId, owner.PropertyId));
        Assert.Single(await mediaService.ListAsync(owner.UserId, owner.PropertyId));
        var storedContent = await mediaService.OpenReadAsync(owner.UserId, mediaId);
        Assert.NotNull(storedContent);
        await storedContent.Content.DisposeAsync();
        Assert.False(await mediaService.DeleteAsync(owner.UserId, owner.PropertyId, mediaId));
        Assert.False(await mediaService.ReorderAsync(owner.UserId, owner.PropertyId, [mediaId]));

        await using var additionalContent = new MemoryStream(OnePixelPng);
        await Assert.ThrowsAsync<InvalidOperationException>(() => mediaService.UploadAsync(
            owner.UserId,
            owner.PropertyId,
            new PropertyMediaUpload("blocked.png", "image/png", additionalContent.Length, additionalContent)));
    }

    private static async Task<(string UserId, Guid PropertyId)> CreateOwnerAndPropertyAsync(
        IServiceProvider services,
        string prefix)
    {
        var registration = services.GetRequiredService<IAccountRegistrationService>();
        var properties = services.GetRequiredService<IPropertyService>();
        var registrationResult = await registration.RegisterAsync(new RegisterAccountCommand(
            $"{prefix}-{Guid.NewGuid():N}@example.com",
            "Password123",
            $"{prefix} Realty {Guid.NewGuid():N}"));
        Assert.True(registrationResult.Succeeded, string.Join(", ", registrationResult.Errors));
        var propertyId = await properties.CreateAsync(registrationResult.UserId!, ValidProperty);
        return (registrationResult.UserId!, propertyId);
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
        ListingStatus.Draft);

    private sealed class CountingStream(byte[] content) : MemoryStream(content)
    {
        public int BytesRead { get; private set; }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            BytesRead += read;
            return read;
        }
    }
}
