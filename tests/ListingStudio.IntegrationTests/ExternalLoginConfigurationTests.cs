using AspNet.Security.OAuth.Apple;
using ListingStudio.Web.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed class ExternalLoginConfigurationTests
{
    [Fact]
    public async Task EnabledProvidersAreRegisteredAsExternalSchemes()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["Authentication:Google:Enabled"] = "true",
            ["Authentication:Google:ClientId"] = "google-client-id",
            ["Authentication:Google:ClientSecret"] = "google-client-secret",
            ["Authentication:Apple:Enabled"] = "true",
            ["Authentication:Apple:ClientId"] = "com.example.listingstudio",
            ["Authentication:Apple:TeamId"] = "APPLETEAM1",
            ["Authentication:Apple:KeyId"] = "APPLEKEY1",
            ["Authentication:Apple:PrivateKey"] = "test-private-key",
        });
        var services = new ServiceCollection();
        services.AddExternalLoginProviders(configuration);
        await using var provider = services.BuildServiceProvider();

        var schemes = await provider.GetRequiredService<IAuthenticationSchemeProvider>()
            .GetAllSchemesAsync();

        Assert.Contains(schemes, scheme =>
            scheme.Name == GoogleDefaults.AuthenticationScheme && scheme.DisplayName == "Google");
        Assert.Contains(schemes, scheme =>
            scheme.Name == AppleAuthenticationDefaults.AuthenticationScheme && scheme.DisplayName == "Apple");
    }

    [Fact]
    public void EnabledProviderRejectsMissingCredentials()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["Authentication:Google:Enabled"] = "true",
            ["Authentication:Google:ClientId"] = "google-client-id",
        });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddExternalLoginProviders(configuration));

        Assert.Contains("Authentication:Google:ClientSecret", exception.Message, StringComparison.Ordinal);
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
