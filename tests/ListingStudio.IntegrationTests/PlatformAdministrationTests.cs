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
        using var adminPage = await client.GetAsync("/admin");
        var adminContent = await adminPage.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, organizationOwner.StatusCode);
        Assert.Equal(HttpStatusCode.OK, platformAdmin.StatusCode);
        Assert.Equal(HttpStatusCode.OK, adminPage.StatusCode);
        Assert.Contains("Average AI/API Cost per Property", adminContent, StringComparison.Ordinal);
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

    [Fact]
    public async Task MetricsResetCreatesAnAuditedBaselineWithoutDeletingHistory()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var resetService = scope.ServiceProvider.GetRequiredService<IPlatformAdminMetricsResetService>();
        var dashboardService = scope.ServiceProvider.GetRequiredService<IPlatformAdminDashboardService>();
        var now = fixture.Factory.TimeProvider.GetUtcNow();
        var propertyOne = new string('a', 64);
        var propertyTwo = new string('b', 64);

        dbContext.OperationalEvents.Add(OperationalEvent.Create(
            "request",
            "legacy-metrics-route",
            "2xx",
            now.AddMinutes(-10)));
        dbContext.AiUsageEvents.Add(AiUsageEvent.Create(
            "legacy-provider",
            "legacy-model",
            "legacy-operation",
            true,
            100,
            42m,
            null,
            now.AddMinutes(-10)));
        await dbContext.SaveChangesAsync();

        var resetAtUtc = await resetService.ResetAsync("metrics-reset-test-admin");
        dbContext.OperationalEvents.Add(OperationalEvent.Create(
            "request",
            "current-metrics-route",
            "2xx",
            resetAtUtc.AddMinutes(1)));
        dbContext.AiUsageEvents.Add(AiUsageEvent.Create(
            "current-provider",
            "current-model",
            "current-operation",
            true,
            50,
            0.25m,
            null,
            resetAtUtc.AddMinutes(1),
            propertyOne));
        dbContext.AiUsageEvents.Add(AiUsageEvent.Create(
            "second-current-provider",
            "current-model",
            "current-operation",
            true,
            75,
            0.75m,
            null,
            resetAtUtc.AddMinutes(1),
            propertyTwo));
        dbContext.AiUsageEvents.Add(AiUsageEvent.Create(
            "unattributed-current-provider",
            "current-model",
            "current-operation",
            true,
            25,
            10m,
            null,
            resetAtUtc.AddMinutes(1)));
        await dbContext.SaveChangesAsync();

        var dashboard = await dashboardService.GetAsync(new AdminDateRange(
            now.AddDays(-1),
            resetAtUtc.AddMinutes(2)));

        Assert.Equal(resetAtUtc, dashboard.MetricsResetAtUtc);
        Assert.DoesNotContain(dashboard.Traffic, row => row.Primary == "legacy-metrics-route");
        Assert.Contains(dashboard.Traffic, row => row.Primary == "current-metrics-route");
        Assert.DoesNotContain(dashboard.AiUsage, row => row.Primary == "legacy-provider");
        Assert.Contains(dashboard.AiUsage, row =>
            row.Primary == "current-provider" && row.EstimatedCostUsd == 0.25m);
        Assert.Equal(11m, dashboard.EstimatedAiCostUsd);
        Assert.Equal(0.50m, dashboard.AverageAiCostPerPropertyUsd);
        Assert.Equal(2, dashboard.AiAttributedPropertyCount);
        Assert.True(await dbContext.OperationalEvents.AnyAsync(
            item => item.Category == "legacy-metrics-route"));
        Assert.True(await dbContext.AiUsageEvents.AnyAsync(
            item => item.Provider == "legacy-provider"));

        var auditEvent = await dbContext.PlatformAdminAuditEvents.SingleAsync(item =>
            item.Action == "platform-metrics.reset" && item.OccurredAtUtc == resetAtUtc);
        Assert.Equal("platform-admin", auditEvent.ActorType);
        Assert.Equal("succeeded", auditEvent.Outcome);
        Assert.Equal(64, auditEvent.TargetReference.Length);
        Assert.DoesNotContain("metrics-reset-test-admin", auditEvent.TargetReference, StringComparison.Ordinal);

        var resetRecord = await dbContext.PlatformMetricsResets.SingleAsync(item => item.ResetAtUtc == resetAtUtc);
        dbContext.PlatformMetricsResets.Remove(resetRecord);
        await Assert.ThrowsAsync<InvalidOperationException>(() => dbContext.SaveChangesAsync());
    }

    [Fact]
    public async Task AiUsageRecorderPersistsTheCurrentPropertyAttribution()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var recorder = scope.ServiceProvider.GetRequiredService<IAiUsageRecorder>();
        var propertyContext = scope.ServiceProvider.GetRequiredService<IAiUsagePropertyContext>();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var propertyId = Guid.NewGuid();
        var operation = $"property-attribution-{Guid.NewGuid():N}";

        using (propertyContext.BeginProperty(propertyId))
        {
            await recorder.RecordAsync(new AiCallObservation(
                "test-provider",
                "test-model",
                operation,
                true,
                TimeSpan.FromMilliseconds(25),
                0.10m));
        }

        var recorded = await dbContext.AiUsageEvents.AsNoTracking()
            .SingleAsync(item => item.Operation == operation);
        Assert.NotNull(recorded.PropertyReference);
        Assert.Equal(64, recorded.PropertyReference.Length);
        Assert.DoesNotContain(propertyId.ToString("N"), recorded.PropertyReference, StringComparison.OrdinalIgnoreCase);

        dbContext.AiUsageEvents.Remove(await dbContext.AiUsageEvents.SingleAsync(item => item.Id == recorded.Id));
        await dbContext.SaveChangesAsync();
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
