using System.Net;
using System.Text.RegularExpressions;
using ListingStudio.Application.Administration;
using ListingStudio.Application.Authentication;
using ListingStudio.Application.Properties;
using ListingStudio.Domain.Properties;
using ListingStudio.Infrastructure.Administration;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed class AdminImpersonationTests(PostgreSqlWebApplicationFixture fixture)
    : IClassFixture<PostgreSqlWebApplicationFixture>
{
    private const string Password = "Password123";

    [Fact]
    public async Task SearchFindsAccountsByEmailUserNameOrWorkspaceWithTheirProperties()
    {
        var marker = Guid.NewGuid().ToString("N")[..10];
        var customer = await RegisterAsync($"search-{marker}@example.com", $"Harbor {marker} Realty");
        var propertyId = await CreatePropertyAsync(customer.UserId, $"{marker} Search Street");
        await RegisterAsync($"other-{Guid.NewGuid():N}@example.com", "Unrelated Realty");

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var directory = scope.ServiceProvider.GetRequiredService<IPlatformAdminAccountDirectory>();

        var byEmail = await directory.SearchAsync($"SEARCH-{marker}");
        var byWorkspace = await directory.SearchAsync($"harbor {marker}");
        var wildcard = await directory.SearchAsync("%");
        var tooShort = await directory.SearchAsync("s");

        var account = Assert.Single(byEmail.Accounts);
        Assert.Equal(customer.Email, account.Email);
        Assert.False(account.IsPlatformAdmin);
        var workspace = Assert.Single(account.Workspaces);
        Assert.Equal($"Harbor {marker} Realty", workspace.Name);
        Assert.Equal("Owner", workspace.Role);
        var property = Assert.Single(workspace.Properties);
        Assert.Equal(propertyId, property.Id);
        Assert.Equal($"{marker} Search Street", property.Address1);
        Assert.Equal(customer.UserId, Assert.Single(byWorkspace.Accounts).UserId);
        Assert.Empty(wildcard.Accounts);
        Assert.Empty(tooShort.Accounts);
    }

    [Fact]
    public async Task AdminCanImpersonateAUserToOpenTheirPropertyAndStopAgain()
    {
        var marker = Guid.NewGuid().ToString("N")[..10];
        var admin = await RegisterAsync($"impersonating-admin-{marker}@example.com", $"Admin {marker}");
        await GrantPlatformAdminAsync(admin.Email);
        var customer = await RegisterAsync($"impersonated-{marker}@example.com", $"Customer {marker}");
        var propertyId = await CreatePropertyAsync(customer.UserId, $"{marker} Harbor Lane");
        using var client = CookieClient();
        await LogInAsync(client, admin.Email);

        using var search = await client.GetAsync($"/admin/users?q={marker}");
        search.EnsureSuccessStatusCode();
        var searchHtml = await search.Content.ReadAsStringAsync();
        Assert.Contains(customer.Email, searchHtml, StringComparison.Ordinal);
        Assert.Contains($"{marker} Harbor Lane", searchHtml, StringComparison.Ordinal);
        Assert.Contains($"/properties/{propertyId}", searchHtml, StringComparison.Ordinal);

        using var start = await client.PostAsync("/admin/impersonation/start", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = HiddenValue(searchHtml, "__RequestVerificationToken"),
            ["userId"] = customer.UserId,
            ["q"] = marker,
            ["returnUrl"] = $"/properties/{propertyId}",
        }));
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        Assert.Equal($"/properties/{propertyId}", start.Headers.Location?.OriginalString);

        using var property = await client.GetAsync($"/properties/{propertyId}");
        property.EnsureSuccessStatusCode();
        var propertyHtml = await property.Content.ReadAsStringAsync();
        Assert.Contains($"You are viewing Creative Listing Studio as {customer.Email}.", propertyHtml, StringComparison.Ordinal);
        Assert.Contains($"{marker} Harbor Lane", propertyHtml, StringComparison.Ordinal);
        Assert.Contains("Stop impersonating", propertyHtml, StringComparison.Ordinal);

        using var adminWhileImpersonating = await client.GetAsync("/admin/users");
        Assert.NotEqual(HttpStatusCode.OK, adminWhileImpersonating.StatusCode);

        using var checkout = await client.PostAsync("/billing/checkout", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = HiddenValue(propertyHtml, "__RequestVerificationToken"),
            ["plan"] = "Basic",
        }));
        Assert.Equal(HttpStatusCode.Forbidden, checkout.StatusCode);

        using var stop = await client.PostAsync("/admin/impersonation/stop", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = HiddenValue(propertyHtml, "__RequestVerificationToken"),
        }));
        Assert.Equal(HttpStatusCode.Redirect, stop.StatusCode);
        Assert.Equal("/admin/users", stop.Headers.Location?.OriginalString);

        using var adminAgain = await client.GetAsync("/admin/users");
        adminAgain.EnsureSuccessStatusCode();
        Assert.DoesNotContain("You are viewing Creative Listing Studio as", await adminAgain.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var events = await dbContext.PlatformAdminAuditEvents.AsNoTracking()
            .Where(item => item.TargetReference == AuditPseudonym.For(customer.UserId))
            .OrderBy(item => item.OccurredAtUtc)
            .ToListAsync();
        Assert.Collection(
            events,
            item => Assert.Equal((PlatformAdminImpersonation.StartAction, "succeeded"), (item.Action, item.Outcome)),
            item => Assert.Equal((PlatformAdminImpersonation.StopAction, "stopped"), (item.Action, item.Outcome)));
        Assert.All(events, item => Assert.Equal(AuditPseudonym.For(admin.UserId), item.ActorReference));
    }

    [Fact]
    public async Task CustomersCannotStartImpersonation()
    {
        var marker = Guid.NewGuid().ToString("N")[..10];
        var customer = await RegisterAsync($"not-admin-{marker}@example.com", $"Plain {marker}");
        var victim = await RegisterAsync($"victim-{marker}@example.com", $"Victim {marker}");
        using var client = CookieClient();
        await LogInAsync(client, customer.Email);
        using var page = await client.GetAsync("/properties");
        var html = await page.Content.ReadAsStringAsync();

        using var start = await client.PostAsync("/admin/impersonation/start", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = HiddenValue(html, "__RequestVerificationToken"),
            ["userId"] = victim.UserId,
            ["returnUrl"] = "/auth",
        }));

        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        Assert.Equal("/Account/AccessDenied", start.Headers.Location?.AbsolutePath);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await dbContext.PlatformAdminAuditEvents.AnyAsync(
            item => item.TargetReference == AuditPseudonym.For(victim.UserId)));
    }

    [Fact]
    public async Task AdministratorsCannotImpersonateThemselvesOrOtherAdministrators()
    {
        var marker = Guid.NewGuid().ToString("N")[..10];
        var first = await RegisterAsync($"first-admin-{marker}@example.com", $"First {marker}");
        var second = await RegisterAsync($"second-admin-{marker}@example.com", $"Second {marker}");
        var customer = await RegisterAsync($"plain-{marker}@example.com", $"Plain {marker}");
        await GrantPlatformAdminAsync(first.Email);
        await GrantPlatformAdminAsync(second.Email);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var audit = scope.ServiceProvider.GetRequiredService<IPlatformAdminImpersonationAudit>();

        Assert.Equal(ImpersonationStartFailure.SelfImpersonation, (await audit.AuthorizeStartAsync(first.UserId, first.UserId)).Failure);
        Assert.Equal(ImpersonationStartFailure.TargetIsPlatformAdmin, (await audit.AuthorizeStartAsync(first.UserId, second.UserId)).Failure);
        Assert.Equal(ImpersonationStartFailure.NotPlatformAdmin, (await audit.AuthorizeStartAsync(customer.UserId, first.UserId)).Failure);
        Assert.Equal(ImpersonationStartFailure.UserNotFound, (await audit.AuthorizeStartAsync(first.UserId, Guid.NewGuid().ToString())).Failure);
        var allowed = await audit.AuthorizeStartAsync(first.UserId, customer.UserId);
        Assert.True(allowed.Succeeded);
        Assert.Equal(customer.Email, allowed.Target!.Email);

        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var denied = await dbContext.PlatformAdminAuditEvents.AsNoTracking()
            .CountAsync(item => item.Action == PlatformAdminImpersonation.StartAction
                && item.Outcome == "denied"
                && item.ActorReference == AuditPseudonym.For(first.UserId));
        Assert.Equal(3, denied);
    }

    private HttpClient CookieClient() => fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
    });

    private static async Task LogInAsync(HttpClient client, string email)
    {
        using var form = await client.GetAsync("/Account/Login");
        form.EnsureSuccessStatusCode();
        var html = await form.Content.ReadAsStringAsync();
        using var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "login",
            ["__RequestVerificationToken"] = HiddenValue(html, "__RequestVerificationToken"),
            ["Input.Email"] = email,
            ["Input.Password"] = Password,
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private async Task<(string UserId, string Email)> RegisterAsync(string email, string workspace)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var registration = scope.ServiceProvider.GetRequiredService<IAccountRegistrationService>();
        var result = await registration.RegisterAsync(new RegisterAccountCommand(email, Password, workspace));
        Assert.True(result.Succeeded);
        return (result.UserId!, email);
    }

    private async Task GrantPlatformAdminAsync(string email)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PlatformAdminRoleBootstrapper>().GrantAsync(email);
    }

    private async Task<Guid> CreatePropertyAsync(string userId, string address)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var properties = scope.ServiceProvider.GetRequiredService<IPropertyService>();
        return await properties.CreateAsync(userId, new PropertyInput(
            address,
            null,
            "Raleigh",
            "NC",
            "27601",
            450_000m,
            3,
            2m,
            1_700,
            0.2m,
            2001,
            PropertyType.SingleFamily,
            "Impersonation test property.",
            ListingStatus.Draft));
    }

    private static string HiddenValue(string html, string name)
    {
        var match = Regex.Match(html, $"name=\"{Regex.Escape(name)}\"[^>]*value=\"([^\"]*)\"");
        Assert.True(match.Success, $"The page has no {name} field.");
        return System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);
    }
}
