using ListingStudio.Application.Authentication;
using ListingStudio.Application.Neighborhoods;
using ListingStudio.Application.Properties;
using ListingStudio.Domain.Neighborhoods;
using ListingStudio.Domain.Properties;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed class NeighborhoodInsightTests(PostgreSqlWebApplicationFixture fixture)
    : IClassFixture<PostgreSqlWebApplicationFixture>
{
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Z+VAAAAAASUVORK5CYII=");

    [Fact]
    public async Task RefreshApprovalAndPhotoAccessAreTenantScoped()
    {
        var provider = new FakeNeighborhoodDataProvider();
        await using var factory = fixture.CreateFactory(services =>
        {
            services.RemoveAll<INeighborhoodDataProvider>();
            services.AddSingleton<INeighborhoodDataProvider>(provider);
        });
        var owner = await CreateOwnerAndPropertyAsync(factory.Services, "neighborhood-owner");
        var outsider = await CreateOwnerAndPropertyAsync(factory.Services, "neighborhood-outsider");

        NeighborhoodInsightResult insight;
        Guid persistedInsightId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<INeighborhoodInsightService>();
            insight = Assert.Single(await service.RefreshAsync(owner.UserId, owner.PropertyId));
            Assert.False(insight.IsApproved);
            Assert.Equal(NeighborhoodPlaceCategory.School, insight.Category);
            Assert.True(await service.SetApprovalAsync(owner.UserId, owner.PropertyId, insight.Id, true));

            var approved = Assert.Single(await service.GetAsync(owner.UserId, owner.PropertyId));
            Assert.True(approved.IsApproved);
            Assert.Empty(await service.GetAsync(outsider.UserId, owner.PropertyId));
            Assert.False(await service.SetApprovalAsync(outsider.UserId, owner.PropertyId, insight.Id, false));
            Assert.Null(await service.OpenPhotoAsync(outsider.UserId, insight.Id));

            var photo = await service.OpenPhotoAsync(owner.UserId, insight.Id);
            Assert.NotNull(photo);
            Assert.Equal("image/jpeg", photo.ContentType);
            await photo.Content.DisposeAsync();

            await using var licensedPhoto = new MemoryStream(OnePixelPng);
            Assert.True(await service.UploadVideoPhotoAsync(
                owner.UserId,
                owner.PropertyId,
                insight.Id,
                new NeighborhoodVideoPhotoUpload(
                    "licensed-park.png",
                    "image/png",
                    OnePixelPng.Length,
                    "Photo © Listing Agent",
                    licensedPhoto)));
            Assert.Null(await service.OpenVideoPhotoAsync(outsider.UserId, insight.Id));
            var videoPhoto = await service.OpenVideoPhotoAsync(owner.UserId, insight.Id);
            Assert.NotNull(videoPhoto);
            Assert.Equal("image/png", videoPhoto.ContentType);
            await videoPhoto.Content.DisposeAsync();

            var refreshed = Assert.Single(await service.RefreshAsync(owner.UserId, owner.PropertyId));
            Assert.True(refreshed.IsApproved);
            Assert.True(refreshed.HasVideoPhoto);
            Assert.Equal("Photo © Listing Agent", refreshed.VideoPhotoCredit);
            persistedInsightId = refreshed.Id;
        }

        Assert.Equal(2, provider.SearchCalls);
        Assert.Equal(1, provider.PhotoCalls);
        await using var verificationScope = factory.Services.CreateAsyncScope();
        var db = verificationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stored = await db.NeighborhoodInsights.AsNoTracking().SingleAsync(item => item.Id == persistedInsightId);
        Assert.Equal(owner.OrganizationId, stored.OrganizationId);
        Assert.True(stored.IsApproved);
    }

    private static async Task<OwnerProperty> CreateOwnerAndPropertyAsync(
        IServiceProvider services,
        string prefix)
    {
        await using var scope = services.CreateAsyncScope();
        var registration = scope.ServiceProvider.GetRequiredService<IAccountRegistrationService>();
        var properties = scope.ServiceProvider.GetRequiredService<IPropertyService>();
        var result = await registration.RegisterAsync(new RegisterAccountCommand(
            $"{prefix}-{Guid.NewGuid():N}@example.com",
            "Password123",
            $"{prefix} Realty {Guid.NewGuid():N}"));
        Assert.True(result.Succeeded, string.Join(", ", result.Errors));
        var propertyId = await properties.CreateAsync(result.UserId!, new PropertyInput(
            "121 E 7th St",
            null,
            "Chuluota",
            "FL",
            "32766",
            420_000m,
            5,
            2m,
            2_100,
            0.84m,
            1965,
            PropertyType.SingleFamily,
            "Single story home.",
            ListingStatus.Active));
        return new OwnerProperty(result.UserId!, result.OrganizationId!.Value, propertyId);
    }

    private sealed record OwnerProperty(string UserId, Guid OrganizationId, Guid PropertyId);

    private sealed class FakeNeighborhoodDataProvider : INeighborhoodDataProvider
    {
        public bool IsConfigured => true;
        public int SearchCalls { get; private set; }
        public int PhotoCalls { get; private set; }

        public Task<IReadOnlyList<NeighborhoodPlaceCandidate>> SearchAsync(
            NeighborhoodSearchRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SearchCalls++;
            Assert.Contains("121 E 7th St", request.FullAddress, StringComparison.Ordinal);
            return Task.FromResult<IReadOnlyList<NeighborhoodPlaceCandidate>>(
            [
                new(
                    "school-place-id",
                    NeighborhoodPlaceCategory.School,
                    "Example Elementary School",
                    "10 Learning Lane, Chuluota, FL 32766",
                    1.2m,
                    "https://maps.google.test/example-school",
                    true,
                    "Example Photographer",
                    "https://maps.google.test/user/example",
                    "https://maps.google.test/photo/example"),
            ]);
        }

        public Task<NeighborhoodPhoto?> OpenPhotoAsync(
            string providerPlaceId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PhotoCalls++;
            Assert.Equal("school-place-id", providerPlaceId);
            return Task.FromResult<NeighborhoodPhoto?>(new NeighborhoodPhoto(
                new MemoryStream([0xFF, 0xD8, 0xFF, 0xD9]),
                "image/jpeg"));
        }
    }
}
