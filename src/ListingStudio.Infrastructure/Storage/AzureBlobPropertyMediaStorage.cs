using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using ListingStudio.Application.Properties;
using ListingStudio.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace ListingStudio.Infrastructure.Storage;

public sealed class AzureBlobPropertyMediaStorage : IPropertyMediaStorage
{
    private readonly BlobContainerClient container;

    public AzureBlobPropertyMediaStorage(IOptions<AzureBlobStorageOptions> options)
    {
        var configuration = options.Value;
        if (string.IsNullOrWhiteSpace(configuration.ConnectionString))
        {
            throw new InvalidOperationException("Azure Blob Storage requires a connection string.");
        }

        if (string.IsNullOrWhiteSpace(configuration.ContainerName))
        {
            throw new InvalidOperationException("Azure Blob Storage requires a container name.");
        }

        container = new BlobContainerClient(configuration.ConnectionString, configuration.ContainerName);
    }

    public async Task StoreAsync(
        string blobPath,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        var blob = container.GetBlobClient(blobPath);
        await blob.UploadAsync(
            content,
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } },
            cancellationToken);
    }

    public async Task<Stream?> OpenReadAsync(string blobPath, CancellationToken cancellationToken = default)
    {
        var blob = container.GetBlobClient(blobPath);
        if (!await blob.ExistsAsync(cancellationToken))
        {
            return null;
        }

        return await blob.OpenReadAsync(cancellationToken: cancellationToken);
    }

    public async Task DeleteAsync(string blobPath, CancellationToken cancellationToken = default)
    {
        await container.GetBlobClient(blobPath).DeleteIfExistsAsync(cancellationToken: cancellationToken);
    }
}
