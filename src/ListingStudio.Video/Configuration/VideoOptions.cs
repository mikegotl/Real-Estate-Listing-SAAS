namespace ListingStudio.Video.Configuration;

public sealed class AIVideoOptions
{
    public const string SectionName = "AIVideo";
    public string Provider { get; init; } = string.Empty;
    public string ApiKey { get; init; } = string.Empty;
    public string Endpoint { get; init; } = string.Empty;
}
