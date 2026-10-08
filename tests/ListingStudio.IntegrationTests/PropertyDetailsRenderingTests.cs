using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using ListingStudio.Application.Authentication;
using ListingStudio.Application.Properties;
using ListingStudio.Domain.Properties;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed class PropertyDetailsRenderingTests(PostgreSqlWebApplicationFixture fixture)
    : IClassFixture<PostgreSqlWebApplicationFixture>
{
    [Fact]
    public async Task DetailsPageRendersIndependentDataPanelsWithoutDbContextConcurrency()
    {
        await using var factory = fixture.CreateFactory(services =>
        {
            services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = HeaderAuthenticationHandler.SchemeName;
                    options.DefaultChallengeScheme = HeaderAuthenticationHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(
                    HeaderAuthenticationHandler.SchemeName,
                    _ => { });
        });
        await using var scope = factory.Services.CreateAsyncScope();
        var registration = scope.ServiceProvider.GetRequiredService<IAccountRegistrationService>();
        var properties = scope.ServiceProvider.GetRequiredService<IPropertyService>();
        var registered = await registration.RegisterAsync(new RegisterAccountCommand(
            $"details-owner-{Guid.NewGuid():N}@example.com",
            "Password123",
            $"Details Realty {Guid.NewGuid():N}"));
        Assert.True(registered.Succeeded, string.Join(", ", registered.Errors));
        var propertyId = await properties.CreateAsync(registered.UserId!, new PropertyInput(
            "121 E 7th St",
            null,
            "Chuluota",
            "FL",
            "32766",
            420_000m,
            3,
            2m,
            1_850,
            0.2m,
            2001,
            PropertyType.SingleFamily,
            "Regression property.",
            ListingStatus.Draft));

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            HeaderAuthenticationHandler.SchemeName,
            registered.UserId);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("de-DE");

        using var response = await client.GetAsync($"/properties/{propertyId}");

        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("121 E 7th St", content, StringComparison.Ordinal);
        Assert.Contains("Listing photos", content, StringComparison.Ordinal);
        Assert.Contains("Marketing story", content, StringComparison.Ordinal);
        Assert.Contains("Campaign", content, StringComparison.Ordinal);
        Assert.Contains("$420,000", content, StringComparison.Ordinal);
        Assert.Contains("1,850", content, StringComparison.Ordinal);
        Assert.Contains("mobile-nav", content, StringComparison.Ordinal);
    }
}

public sealed class HeaderAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "IntegrationHeader";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!AuthenticationHeaderValue.TryParse(Request.Headers.Authorization, out var authorization)
            || !string.Equals(authorization.Scheme, SchemeName, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(authorization.Parameter))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, authorization.Parameter)],
            SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
