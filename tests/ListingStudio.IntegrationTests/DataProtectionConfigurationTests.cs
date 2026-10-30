using ListingStudio.Infrastructure.DependencyInjection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed class DataProtectionConfigurationTests
{
    [Fact]
    public void DevelopmentUsesFrameworkDataProtectionWithoutAzureSettings()
    {
        var services = new ServiceCollection();

        services.AddListingStudioDataProtection(
            Configuration([]),
            Environment(Environments.Development));

        Assert.NotEmpty(services);
    }

    [Theory]
    [InlineData("AZURE_CLIENT_ID")]
    [InlineData("DataProtection:BlobUri")]
    [InlineData("DataProtection:KeyVaultKeyIdentifier")]
    public void ProductionRejectsMissingPersistentKeySetting(string missingSetting)
    {
        var values = ProductionSettings();
        values[missingSetting] = string.Empty;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddListingStudioDataProtection(
                Configuration(values),
                Environment(Environments.Production)));

        Assert.Contains(missingSetting, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("DataProtection:BlobUri")]
    [InlineData("DataProtection:KeyVaultKeyIdentifier")]
    public void ProductionRejectsNonHttpsPersistentKeyUri(string setting)
    {
        var values = ProductionSettings();
        values[setting] = "http://example.test/key";

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddListingStudioDataProtection(
                Configuration(values),
                Environment(Environments.Production)));

        Assert.Contains("absolute HTTPS URI", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionAcceptsManagedIdentityBackedPersistentKeySettings()
    {
        var services = new ServiceCollection();

        services.AddListingStudioDataProtection(
            Configuration(ProductionSettings()),
            Environment(Environments.Production));
        using var provider = services.BuildServiceProvider();
        var keyManagement = provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value;

        Assert.Contains("AzureBlob", keyManagement.XmlRepository?.GetType().Name, StringComparison.Ordinal);
        Assert.Contains("AzureKeyVault", keyManagement.XmlEncryptor?.GetType().Name, StringComparison.Ordinal);
    }

    private static Dictionary<string, string?> ProductionSettings() => new()
    {
        ["AZURE_CLIENT_ID"] = "00000000-0000-0000-0000-000000000001",
        ["DataProtection:ApplicationName"] = "ListingStudio.Web",
        ["DataProtection:BlobUri"] = "https://storage.example.test/data-protection/keys.xml",
        ["DataProtection:KeyVaultKeyIdentifier"] = "https://vault.example.test/keys/data-protection",
    };

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static TestHostEnvironment Environment(string name) => new TestHostEnvironment
    {
        EnvironmentName = name,
    };

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = string.Empty;

        public string ApplicationName { get; set; } = "ListingStudio.IntegrationTests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
