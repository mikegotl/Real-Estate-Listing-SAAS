using System.Net;
using ListingStudio.Application.Authentication;
using ListingStudio.Domain.Organizations;
using ListingStudio.Infrastructure.Identity;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ListingStudio.Domain.Billing;

namespace ListingStudio.IntegrationTests;

public sealed class AuthenticationTests(PostgreSqlWebApplicationFixture fixture)
    : IClassFixture<PostgreSqlWebApplicationFixture>
{
    [Fact]
    public async Task RegistrationCreatesUserOrganizationAndOwnerMembership()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var registration = scope.ServiceProvider.GetRequiredService<IAccountRegistrationService>();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var result = await registration.RegisterAsync(
            new RegisterAccountCommand("owner@example.com", "Password123", "Acme Realty"));

        Assert.True(result.Succeeded, string.Join(", ", result.Errors));
        var user = await userManager.FindByEmailAsync("owner@example.com");
        Assert.NotNull(user);
        Assert.True(await userManager.CheckPasswordAsync(user, "Password123"));

        var organization = await dbContext.Organizations.SingleAsync(
            organization => organization.Id == result.OrganizationId);
        var membership = await dbContext.OrganizationMembers.SingleAsync(
            membership => membership.OrganizationId == result.OrganizationId);
        Assert.Equal("Acme Realty", organization.Name);
        Assert.Equal(organization.Id, membership.OrganizationId);
        Assert.Equal(user.Id, membership.UserId);
        Assert.Equal(OrganizationMemberRole.Owner, membership.Role);
        var billing = await dbContext.OrganizationBillingAccounts.SingleAsync(
            account => account.OrganizationId == organization.Id);
        Assert.Equal(SubscriptionPlan.Free, billing.Plan);
        Assert.Equal(BillingSubscriptionStatus.None, billing.SubscriptionStatus);
    }

    [Fact]
    public async Task RegisteredCredentialsCanLogIn()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var registration = scope.ServiceProvider.GetRequiredService<IAccountRegistrationService>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var signInManager = scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>();

        var registrationResult = await registration.RegisterAsync(
            new RegisterAccountCommand("login@example.com", "Password123", "Login Realty"));
        Assert.True(registrationResult.Succeeded, string.Join(", ", registrationResult.Errors));

        var user = await userManager.FindByEmailAsync("login@example.com");
        Assert.NotNull(user);
        var signInResult = await signInManager.CheckPasswordSignInAsync(user, "Password123", lockoutOnFailure: true);

        Assert.True(signInResult.Succeeded);
    }

    [Fact]
    public async Task ExternalRegistrationCreatesOwnerWithoutLocalPassword()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var registration = scope.ServiceProvider.GetRequiredService<IAccountRegistrationService>();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var result = await registration.RegisterExternalAsync(
            new RegisterExternalAccountCommand(
                "google-owner@example.com",
                "Social Realty",
                "Google",
                "google-subject-123",
                "Google"));

        Assert.True(result.Succeeded, string.Join(", ", result.Errors));
        var user = await userManager.FindByLoginAsync("Google", "google-subject-123");
        Assert.NotNull(user);
        Assert.Equal("google-owner@example.com", user.Email);
        Assert.Null(user.PasswordHash);
        var membership = await dbContext.OrganizationMembers.SingleAsync(
            membership => membership.OrganizationId == result.OrganizationId);
        Assert.Equal(user.Id, membership.UserId);
        Assert.Equal(OrganizationMemberRole.Owner, membership.Role);
        Assert.Contains(
            await userManager.GetLoginsAsync(user),
            login => login.LoginProvider == "Google" && login.ProviderKey == "google-subject-123");
    }

    [Fact]
    public async Task ExternalRegistrationDoesNotLinkAnExistingEmail()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var registration = scope.ServiceProvider.GetRequiredService<IAccountRegistrationService>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var passwordRegistration = await registration.RegisterAsync(
            new RegisterAccountCommand("existing@example.com", "Password123", "Existing Realty"));
        Assert.True(passwordRegistration.Succeeded, string.Join(", ", passwordRegistration.Errors));

        var externalRegistration = await registration.RegisterExternalAsync(
            new RegisterExternalAccountCommand(
                "existing@example.com",
                "Attacker Realty",
                "Google",
                "untrusted-google-subject",
                "Google"));

        Assert.False(externalRegistration.Succeeded);
        var existingUser = await userManager.FindByEmailAsync("existing@example.com");
        Assert.NotNull(existingUser);
        Assert.Empty(await userManager.GetLoginsAsync(existingUser));
        Assert.Null(await userManager.FindByLoginAsync("Google", "untrusted-google-subject"));
    }

    [Fact]
    public async Task UserCanResetPassword()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var registration = scope.ServiceProvider.GetRequiredService<IAccountRegistrationService>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var registrationResult = await registration.RegisterAsync(
            new RegisterAccountCommand("reset@example.com", "Password123", "Reset Realty"));
        Assert.True(registrationResult.Succeeded, string.Join(", ", registrationResult.Errors));

        var user = await userManager.FindByEmailAsync("reset@example.com");
        Assert.NotNull(user);
        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var resetResult = await userManager.ResetPasswordAsync(user, token, "NewPassword456");

        Assert.True(resetResult.Succeeded);
        Assert.False(await userManager.CheckPasswordAsync(user, "Password123"));
        Assert.True(await userManager.CheckPasswordAsync(user, "NewPassword456"));
    }

    [Fact]
    public async Task ProtectedPageRedirectsAnonymousUserToLogin()
    {
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        using var response = await client.GetAsync("/auth");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Account/Login", response.Headers.Location?.AbsolutePath);
    }

    [Fact]
    public async Task HomePageIsAvailable()
    {
        using var client = fixture.Factory.CreateClient();

        using var response = await client.GetAsync("/");

        response.EnsureSuccessStatusCode();
        Assert.Contains("Listing Studio", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisabledExternalProvidersAreNotOfferedOnLoginPage()
    {
        using var client = fixture.Factory.CreateClient();

        using var response = await client.GetAsync("/Account/Login");

        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Continue with Google", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Continue with Apple", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnabledExternalProvidersAreOfferedOnLoginAndRegistrationPages()
    {
        await using var factory = fixture.CreateFactory(services =>
        {
            services.AddAuthentication()
                .AddGoogle("Google", "Google", options =>
                {
                    options.ClientId = "google-client-id";
                    options.ClientSecret = "google-client-secret";
                })
                .AddApple("Apple", "Apple", options =>
                {
                    options.ClientId = "com.example.listingstudio";
                    options.TeamId = "APPLETEAM1";
                    options.KeyId = "APPLEKEY1";
                    options.GenerateClientSecret = true;
                    options.PrivateKey = (_, _) =>
                        Task.FromResult<ReadOnlyMemory<char>>("test-private-key".AsMemory());
                });
        });
        using var client = factory.CreateClient();

        using var loginResponse = await client.GetAsync("/Account/Login");
        using var registrationResponse = await client.GetAsync("/Account/Register");

        loginResponse.EnsureSuccessStatusCode();
        registrationResponse.EnsureSuccessStatusCode();
        var loginContent = await loginResponse.Content.ReadAsStringAsync();
        var registrationContent = await registrationResponse.Content.ReadAsStringAsync();
        Assert.Contains("Continue with Google", loginContent, StringComparison.Ordinal);
        Assert.Contains("Continue with Apple", loginContent, StringComparison.Ordinal);
        Assert.Contains("Continue with Google", registrationContent, StringComparison.Ordinal);
        Assert.Contains("Continue with Apple", registrationContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LivenessAndReadinessEndpointsReportHealthyDependencies()
    {
        using var client = fixture.Factory.CreateClient();

        using var live = await client.GetAsync("/health/live");
        using var ready = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }
}
