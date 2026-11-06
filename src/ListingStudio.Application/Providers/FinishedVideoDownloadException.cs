namespace ListingStudio.Application.Providers;

/// <summary>
/// The provider finished (and charged for) a video, but the finished file could not be downloaded. The provider request
/// must be kept so a retry downloads the same video again instead of paying for a new one.
/// </summary>
public sealed class FinishedVideoDownloadException : IOException
{
    public FinishedVideoDownloadException(string provider, Exception innerException)
        : base(
            $"{provider} finished the video, but it could not be downloaded ({Describe(innerException)}). "
            + "Retry to download it again; no new video is made or charged.",
            innerException)
    {
    }

    private static string Describe(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: { } status } => $"HTTP {(int)status}",
        HttpRequestException => "the connection failed",
        TaskCanceledException => "the download timed out",
        _ => "the file was unusable",
    };
}
