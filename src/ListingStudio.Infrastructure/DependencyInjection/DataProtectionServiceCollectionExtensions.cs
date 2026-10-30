using Azure.Core;
using Azure.Identity;
using ListingStudio.Infrastructure.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ListingStudioDataProtectionOptions = ListingStudio.Infrastructure.Configuration.DataProtectionOptions;

namespace ListingStudio.Infrastructure.DependencyInjection;

public static class DataProtectionServiceCollectionExtensions
{
    public static IServiceCollection AddListingStudioDataProtection(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var options = configuration.GetSection(ListingStudioDataProtectionOptions.SectionName)
            .Get<ListingStudioDataProtectionOptions>() ?? new ListingStudioDataProtectionOptions();
        var builder = services.AddDataProtection()
            .SetApplicationName(Required(options.ApplicationName, "DataProtection:ApplicationName"));

        if (!environment.IsProduction())
        {
            return services;
        }

        var managedIdentityClientId = Required(configuration["AZURE_CLIENT_ID"], "AZURE_CLIENT_ID");
        var blobUri = HttpsUri(options.BlobUri, "DataProtection:BlobUri");
        var keyIdentifier = HttpsUri(options.KeyVaultKeyIdentifier, "DataProtection:KeyVaultKeyIdentifier");
        TokenCredential credential = new ManagedIdentityCredential(
            ManagedIdentityId.FromUserAssignedClientId(managedIdentityClientId));

        builder
            .PersistKeysToAzureBlobStorage(blobUri, credential)
            .ProtectKeysWithAzureKeyVault(keyIdentifier, credential);

        return services;
    }

    private static string Required(string? value, string settingName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Production data protection requires {settingName}.");
        }

        return value;
    }

    private static Uri HttpsUri(string? value, string settingName)
    {
        var required = Required(value, settingName);
        if (!Uri.TryCreate(required, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Production data protection requires {settingName} to be an absolute HTTPS URI.");
        }

        return uri;
    }
}
