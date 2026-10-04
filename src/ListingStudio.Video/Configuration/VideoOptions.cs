namespace ListingStudio.Video.Configuration;

public sealed class AIVideoOptions
{
    public const string SectionName = "AIVideo";
    public bool Enabled { get; init; }
    public string Provider { get; init; } = string.Empty;
    public string ApiKey { get; init; } = string.Empty;
    public string Endpoint { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string GenerationVersion { get; init; } = "ai-video-v1";
    public int RequestTimeoutSeconds { get; init; } = 600;
    public int MaxOutputMegabytes { get; init; } = 100;
}
