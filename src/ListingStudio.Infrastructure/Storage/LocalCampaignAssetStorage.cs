using ListingStudio.Application.Audio;
using ListingStudio.Infrastructure.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ListingStudio.Infrastructure.Storage;

public sealed class LocalCampaignAssetStorage : ICampaignAssetStorage
{
    private readonly string rootPath;

    public LocalCampaignAssetStorage(IOptions<AzureBlobStorageOptions> options, IHostEnvironment environment)
    {
        var configuredPath = options.Value.CampaignLocalRootPath;
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredPath);
        rootPath = Path.GetFullPath(configuredPath, environment.ContentRootPath);
    }

    public async Task StoreAsync(
        string assetPath,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        var path = Resolve(assetPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var target = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await content.CopyToAsync(target, cancellationToken);
    }

    public Task<Stream?> OpenReadAsync(string assetPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Resolve(assetPath);
        Stream? stream = File.Exists(path)
            ? new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81_920,
                FileOptions.Asynchronous | FileOptions.SequentialScan)
            : null;
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string assetPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Resolve(assetPath);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string Resolve(string assetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetPath);
        var path = Path.GetFullPath(assetPath.Replace('/', Path.DirectorySeparatorChar), rootPath);
        var rootPrefix = rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? rootPath
            : rootPath + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The campaign asset path is outside the configured storage root.");
        }

        return path;
    }
}
