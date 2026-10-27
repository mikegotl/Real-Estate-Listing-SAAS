namespace ListingStudio.Video.Configuration;

public sealed class HeyGenOptions
{
    public const string SectionName = "HeyGen";

    public bool Enabled { get; init; }
    public string ApiKey { get; init; } = string.Empty;
    public string BaseUrl { get; init; } = "https://api.heygen.com";
    public string Model { get; init; } = "avatar_iv";
    public string GenerationVersion { get; init; } = "agent-presenter-v1";
    public string Resolution { get; init; } = "720p";
    public int IntroMaximumSeconds { get; init; } = 6;
    public int OutroMaximumSeconds { get; init; } = 4;
    public int PollIntervalSeconds { get; init; } = 5;
    public int RequestTimeoutSeconds { get; init; } = 900;
    public int MaxOutputMegabytes { get; init; } = 100;
    public decimal EstimatedCostPerMinuteUsd { get; init; } = 2.31m;
}
