using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using ListingStudio.Application.Administration;
using ListingStudio.Application.Authentication;
using ListingStudio.Application.Properties;
using ListingStudio.Domain.Administration;
using ListingStudio.Domain.Properties;
using ListingStudio.Infrastructure.Administration;
using ListingStudio.Infrastructure.Identity;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed class PlatformAdministrationTests(PostgreSqlWebApplicationFixture fixture)
    : IClassFixture<PostgreSqlWebApplicationFixture>
{
    [Fact]
    public async Task AdminApiRequiresTheDedicatedPlatformRole()
    {
        await using var factory = fixture.CreateFactory(services =>
        {
            services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = PlatformAdminHeaderAuthenticationHandler.SchemeName;
                    options.DefaultChallengeScheme = PlatformAdminHeaderAuthenticationHandler.SchemeName;
                    options.DefaultForbidScheme = PlatformAdminHeaderAuthenticationHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, PlatformAdminHeaderAuthenticationHandler>(
                    PlatformAdminHeaderAuthenticationHandler.SchemeName,
                    _ => { });
        });
        using var client = factory.CreateClient();

        using var anonymous = await client.GetAsync("/api/admin/dashboard");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            PlatformAdminHeaderAuthenticationHandler.SchemeName,
            "organization-owner");
        using var organizationOwner = await client.GetAsync("/api/admin/dashboard");
        client.DefaultRequestHeaders.Add(
            PlatformAdminHeaderAuthenticationHandler.RoleHeader,
            PlatformAdministration.Role);
        using var platformAdmin = await client.GetAsync("/api/admin/dashboard");

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, organizationOwner.StatusCode);
        Assert.Equal(HttpStatusCode.OK, platformAdmin.StatusCode);
    }

    [Fact]
    public async Task PlatformRoleDoesNotCreateMembershipOrBypassTenantIsolation()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var registration = scope.ServiceProvider.GetRequiredService<IAccountRegistrationService>();
        var properties = scope.ServiceProvider.GetRequiredService<IPropertyService>();
        var bootstrapper = scope.ServiceProvider.GetRequiredService<PlatformAdminRoleBootstrapper>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var adminEmail = $"platform-admin-{Guid.NewGuid():N}@example.com";
        var admin = await registration.RegisterAsync(new RegisterAccountCommand(
            adminEmail,
            "Password123",
            $"Admin workspace {Guid.NewGuid():N}"));
        var customer = await registration.RegisterAsync(new RegisterAccountCommand(
            $"customer-{Guid.NewGuid():N}@example.com",
            "Password123",
            $"Customer workspace {Guid.NewGuid():N}"));
        Assert.True(admin.Succeeded);
        Assert.True(customer.Succeeded);

        var customerPropertyId = await properties.CreateAsync(customer.UserId!, ValidProperty);
        var membershipsBefore = await dbContext.OrganizationMembers.CountAsync(
            item => item.UserId == admin.UserId);

        Assert.True(await bootstrapper.GrantAsync(adminEmail));
        var adminUser = await userManager.FindByIdAsync(admin.UserId!);
        Assert.NotNull(adminUser);
        Assert.True(await userManager.IsInRoleAsync(adminUser, PlatformAdministration.Role));
        Assert.Equal(membershipsBefore, await dbContext.OrganizationMembers.CountAsync(
            item => item.UserId == admin.UserId));
        Assert.Null(await properties.GetAsync(admin.UserId!, customerPropertyId));
        Assert.DoesNotContain(
            await properties.ListAsync(admin.UserId!, includeArchived: true),
            item => item.Id == customerPropertyId);
    }

    [Fact]
    public async Task SensitiveRoleChangesCreateAppendOnlyPseudonymousAuditEvents()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var registration = scope.ServiceProvider.GetRequiredService<IAccountRegistrationService>();
        var bootstrapper = scope.ServiceProvider.GetRequiredService<PlatformAdminRoleBootstrapper>();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var email = $"audited-admin-{Guid.NewGuid():N}@example.com";
        var registrationResult = await registration.RegisterAsync(new RegisterAccountCommand(
            email,
            "Password123",
            $"Audit workspace {Guid.NewGuid():N}"));
        Assert.True(registrationResult.Succeeded);

        Assert.True(await bootstrapper.GrantAsync(email));
        var auditEvent = await dbContext.PlatformAdminAuditEvents
            .Where(item => item.Action == "platform-admin.grant"
                && item.Outcome == "succeeded"
                && item.TargetReference.Length == 64)
            .OrderByDescending(item => item.OccurredAtUtc)
            .FirstAsync();
        Assert.DoesNotContain(email, auditEvent.TargetReference, StringComparison.OrdinalIgnoreCase);

        dbContext.PlatformAdminAuditEvents.Remove(auditEvent);
        await Assert.ThrowsAsync<InvalidOperationException>(() => dbContext.SaveChangesAsync());
    }

    private static PropertyInput ValidProperty => new(
        "100 Tenant Boundary Lane",
        null,
        "Raleigh",
        "NC",
        "27601",
        500_000m,
        3,
        2m,
        1_800,
        0.2m,
        2005,
        PropertyType.SingleFamily,
        "Tenant isolation test property.",
        ListingStatus.Draft);
}

public sealed class PlatformAdminHeaderAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "PlatformAdminIntegrationHeader";
    public const string RoleHeader = "X-Test-Platform-Role";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!AuthenticationHeaderValue.TryParse(Request.Headers.Authorization, out var authorization)
            || !string.Equals(authorization.Scheme, SchemeName, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(authorization.Parameter))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, authorization.Parameter),
        };
        if (Request.Headers.TryGetValue(RoleHeader, out var role) && !string.IsNullOrWhiteSpace(role))
        {
            claims.Add(new Claim(ClaimTypes.Role, role.ToString()));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
