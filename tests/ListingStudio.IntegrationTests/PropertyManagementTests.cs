using System.Net;
using ListingStudio.Application.Authentication;
using ListingStudio.Application.Properties;
using ListingStudio.Domain.Properties;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed class PropertyManagementTests(PostgreSqlWebApplicationFixture fixture)
    : IClassFixture<PostgreSqlWebApplicationFixture>
{
    [Fact]
    public async Task PropertyDashboardRedirectsAnonymousUserToLogin()
    {
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        using var response = await client.GetAsync("/properties");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Account/Login", response.Headers.Location?.AbsolutePath);
    }

    [Fact]
    public async Task OwnerCanCreateReadUpdateAndArchiveProperty()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var registration = scope.ServiceProvider.GetRequiredService<IAccountRegistrationService>();
        var properties = scope.ServiceProvider.GetRequiredService<IPropertyService>();
        var email = $"property-owner-{Guid.NewGuid():N}@example.com";
        var registered = await registration.RegisterAsync(
            new RegisterAccountCommand(email, "Password123", $"Property Realty {Guid.NewGuid():N}"));
        Assert.True(registered.Succeeded, string.Join(", ", registered.Errors));

        var propertyId = await properties.CreateAsync(registered.UserId!, ValidInput);
        var created = await properties.GetAsync(registered.UserId!, propertyId);

        Assert.NotNull(created);
        Assert.Equal("123 Main Street", created.Address1);
        Assert.Contains(await properties.ListAsync(registered.UserId!), item => item.Id == propertyId);

        var updated = await properties.UpdateAsync(
            registered.UserId!,
            propertyId,
            ValidInput with { ListingStatus = ListingStatus.Active, ListingPrice = 475_000m });
        Assert.True(updated);
        var edited = await properties.GetAsync(registered.UserId!, propertyId);
        Assert.NotNull(edited);
        Assert.Equal(ListingStatus.Active, edited.ListingStatus);
        Assert.Equal(475_000m, edited.ListingPrice);

        Assert.True(await properties.ArchiveAsync(registered.UserId!, propertyId));
        Assert.DoesNotContain(await properties.ListAsync(registered.UserId!), item => item.Id == propertyId);
        Assert.Contains(await properties.ListAsync(registered.UserId!, includeArchived: true), item => item.Id == propertyId);
    }

    [Fact]
    public async Task PropertyAccessIsRestrictedToOwningOrganization()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var registration = scope.ServiceProvider.GetRequiredService<IAccountRegistrationService>();
        var properties = scope.ServiceProvider.GetRequiredService<IPropertyService>();
        var owner = await registration.RegisterAsync(new RegisterAccountCommand(
            $"tenant-a-{Guid.NewGuid():N}@example.com", "Password123", $"Tenant A {Guid.NewGuid():N}"));
        var outsider = await registration.RegisterAsync(new RegisterAccountCommand(
            $"tenant-b-{Guid.NewGuid():N}@example.com", "Password123", $"Tenant B {Guid.NewGuid():N}"));
        Assert.True(owner.Succeeded, string.Join(", ", owner.Errors));
        Assert.True(outsider.Succeeded, string.Join(", ", outsider.Errors));

        var propertyId = await properties.CreateAsync(owner.UserId!, ValidInput);

        Assert.Null(await properties.GetAsync(outsider.UserId!, propertyId));
        Assert.DoesNotContain(await properties.ListAsync(outsider.UserId!, includeArchived: true), item => item.Id == propertyId);
        Assert.False(await properties.UpdateAsync(
            outsider.UserId!, propertyId, ValidInput with { Address1 = "Unauthorized change" }));
        Assert.False(await properties.ArchiveAsync(outsider.UserId!, propertyId));

        var unchanged = await properties.GetAsync(owner.UserId!, propertyId);
        Assert.NotNull(unchanged);
        Assert.Equal("123 Main Street", unchanged.Address1);
        Assert.False(unchanged.IsArchived);
    }

    private static PropertyInput ValidInput => new(
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
