using ListingStudio.Application.Properties;
using ListingStudio.Infrastructure.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ListingStudio.Infrastructure.Storage;

public sealed class LocalPropertyMediaStorage : IPropertyMediaStorage
{
    private readonly string rootPath;

    public LocalPropertyMediaStorage(IOptions<AzureBlobStorageOptions> options, IHostEnvironment environment)
    {
        var configuredPath = options.Value.LocalRootPath;
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredPath);
        rootPath = Path.GetFullPath(configuredPath, environment.ContentRootPath);
    }

    public async Task StoreAsync(
        string blobPath,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        var path = Resolve(blobPath);
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

    public Task<Stream?> OpenReadAsync(string blobPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Resolve(blobPath);
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

    public Task DeleteAsync(string blobPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Resolve(blobPath);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string Resolve(string blobPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blobPath);
        var path = Path.GetFullPath(blobPath.Replace('/', Path.DirectorySeparatorChar), rootPath);
        var rootPrefix = rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? rootPath
            : rootPath + Path.DirectorySeparatorChar;

        if (!path.StartsWith(rootPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The media path is outside the configured storage root.");
        }

        return path;
    }
}
