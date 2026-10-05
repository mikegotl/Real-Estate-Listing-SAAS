using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Identity;
using ListingStudio.Application.Audio;
using ListingStudio.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace ListingStudio.Infrastructure.Storage;

public sealed class AzureBlobCampaignAssetStorage : ICampaignAssetStorage
{
    private readonly BlobContainerClient container;

    public AzureBlobCampaignAssetStorage(IOptions<AzureBlobStorageOptions> options)
    {
        var configuration = options.Value;
        if (string.IsNullOrWhiteSpace(configuration.ContainerName))
        {
            throw new InvalidOperationException("Azure Blob Storage requires a container name.");
        }

        container = CreateContainer(configuration);
    }

    public async Task StoreAsync(
        string assetPath,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        await container.GetBlobClient(assetPath).UploadAsync(
            content,
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } },
            cancellationToken);
    }

    public async Task<Stream?> OpenReadAsync(string assetPath, CancellationToken cancellationToken = default)
    {
        var blob = container.GetBlobClient(assetPath);
        return await blob.ExistsAsync(cancellationToken)
            ? await blob.OpenReadAsync(cancellationToken: cancellationToken)
            : null;
    }

    public async Task DeleteAsync(string assetPath, CancellationToken cancellationToken = default)
    {
        await container.GetBlobClient(assetPath).DeleteIfExistsAsync(cancellationToken: cancellationToken);
    }

    private static BlobContainerClient CreateContainer(AzureBlobStorageOptions configuration)
    {
        if (!string.IsNullOrWhiteSpace(configuration.ConnectionString))
        {
            return new BlobContainerClient(configuration.ConnectionString, configuration.ContainerName);
        }

        if (Uri.TryCreate(configuration.ServiceUri, UriKind.Absolute, out var serviceUri)
            && serviceUri.Scheme == Uri.UriSchemeHttps)
        {
            return new BlobServiceClient(serviceUri, new DefaultAzureCredential())
                .GetBlobContainerClient(configuration.ContainerName);
        }

        throw new InvalidOperationException(
            "Azure Blob Storage requires either a connection string or an HTTPS service URI for managed identity.");
    }
}
