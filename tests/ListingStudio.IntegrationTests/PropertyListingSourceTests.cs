using ListingStudio.Application.Authentication;
using ListingStudio.Application.Properties;
using ListingStudio.Domain.Properties;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed class PropertyListingSourceTests(PostgreSqlWebApplicationFixture fixture)
    : IClassFixture<PostgreSqlWebApplicationFixture>
{
    [Fact]
    public async Task OwnerCanLinkReplaceAndRemoveZillowResearchPage()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var owner = await CreateOwnerAndPropertyAsync(scope.ServiceProvider, "listing-link-owner");
        var service = scope.ServiceProvider.GetRequiredService<IPropertyListingSourceService>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.LinkZillowAsync(
            owner.UserId,
            owner.PropertyId,
            "https://www.zillow.com/homedetails/123-Main-St/123_zpid/",
            addressConfirmed: false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.LinkZillowAsync(
            owner.UserId,
            owner.PropertyId,
            "https://www.zillow.com/homedetails/999-Oak-St-Raleigh-NC-27602/999_zpid/",
            addressConfirmed: true));

        var linked = await service.LinkZillowAsync(
            owner.UserId,
            owner.PropertyId,
            "https://zillow.com/homedetails/123-Main-St-Raleigh-NC-27601/123_zpid/?utm_source=test",
            addressConfirmed: true);

        Assert.NotNull(linked);
        Assert.Equal("123", linked.ExternalListingId);
        Assert.Equal(
            "https://www.zillow.com/homedetails/123-Main-St-Raleigh-NC-27601/123_zpid/",
            linked.SourceUrl);
        Assert.Equal(linked, await service.GetAsync(owner.UserId, owner.PropertyId));

        var replaced = await service.LinkZillowAsync(
            owner.UserId,
            owner.PropertyId,
            "https://www.zillow.com/homedetails/123-Main-St-Raleigh-NC-27601/456_zpid/",
            addressConfirmed: true);
        Assert.NotNull(replaced);
        Assert.Equal(linked.Id, replaced.Id);
        Assert.Equal("456", replaced.ExternalListingId);

        Assert.True(await service.RemoveAsync(owner.UserId, owner.PropertyId));
        Assert.Null(await service.GetAsync(owner.UserId, owner.PropertyId));
    }

    [Fact]
    public async Task ListingSourceAccessIsRestrictedToTheOwningOrganization()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var owner = await CreateOwnerAndPropertyAsync(scope.ServiceProvider, "listing-link-tenant-a");
        var outsider = await CreateOwnerAndPropertyAsync(scope.ServiceProvider, "listing-link-tenant-b");
        var service = scope.ServiceProvider.GetRequiredService<IPropertyListingSourceService>();
        await service.LinkZillowAsync(
            owner.UserId,
            owner.PropertyId,
            "https://www.zillow.com/homedetails/123-Main-St-Raleigh-NC-27601/123_zpid/",
            addressConfirmed: true);

        Assert.Null(await service.GetAsync(outsider.UserId, owner.PropertyId));
        Assert.Null(await service.LinkZillowAsync(
            outsider.UserId,
            owner.PropertyId,
            "https://www.zillow.com/homedetails/123-Main-St-Raleigh-NC-27601/456_zpid/",
            addressConfirmed: true));
        Assert.False(await service.RemoveAsync(outsider.UserId, owner.PropertyId));

        var unchanged = await service.GetAsync(owner.UserId, owner.PropertyId);
        Assert.NotNull(unchanged);
        Assert.Equal("123", unchanged.ExternalListingId);
    }

    private static async Task<(string UserId, Guid PropertyId)> CreateOwnerAndPropertyAsync(
        IServiceProvider services,
        string prefix)
    {
        var registration = services.GetRequiredService<IAccountRegistrationService>();
        var registered = await registration.RegisterAsync(new RegisterAccountCommand(
            $"{prefix}-{Guid.NewGuid():N}@example.com",
            "Password123",
            $"{prefix} {Guid.NewGuid():N}"));
        Assert.True(registered.Succeeded, string.Join(", ", registered.Errors));
        var propertyId = await services.GetRequiredService<IPropertyService>().CreateAsync(
            registered.UserId!,
            new PropertyInput(
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
                ListingStatus.Draft));
        return (registered.UserId!, propertyId);
    }
}
