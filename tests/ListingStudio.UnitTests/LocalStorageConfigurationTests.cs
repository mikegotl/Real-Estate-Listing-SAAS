using System.Text.Json;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class LocalStorageConfigurationTests
{
    [Theory]
    [InlineData("LocalRootPath")]
    [InlineData("CampaignLocalRootPath")]
    public void WebAndWorkerResolveLocalStorageToTheSameFolder(string setting)
    {
        var repositoryRoot = FindRepositoryRoot();
        var webProject = Path.Combine(repositoryRoot, "src", "ListingStudio.Web");
        var workerProject = Path.Combine(repositoryRoot, "src", "ListingStudio.Worker");

        var webPath = Path.GetFullPath(ReadStorageSetting(webProject, setting), webProject);
        var workerPath = Path.GetFullPath(ReadStorageSetting(workerProject, setting), workerProject);

        Assert.Equal(webPath, workerPath);
    }

    private static string ReadStorageSetting(string projectDirectory, string setting)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(projectDirectory, "appsettings.json")));
        return document.RootElement.GetProperty("AzureBlobStorage").GetProperty(setting).GetString()!;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ListingStudio.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("ListingStudio.slnx was not found.");
    }
}
