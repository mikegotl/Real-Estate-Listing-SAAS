namespace ListingStudio.Video.Configuration;

/// <summary>Higgsfield image-to-video settings for storyboard walk-through clips.</summary>
public sealed class HiggsfieldOptions
{
    public const string SectionName = "Higgsfield";

    public bool Enabled { get; init; }

    /// <summary>The API key ID from the Higgsfield console.</summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>The API key secret paired with <see cref="ApiKey"/>.</summary>
    public string ApiSecret { get; init; } = string.Empty;

    public string BaseUrl { get; init; } = "https://api.higgsfield.ai";

    /// <summary>An image-to-video model endpoint that accepts both a first and a last frame.</summary>
    public string Model { get; init; } = "kling-video/v3.0/std/image-to-video";

    public int ClipDurationSeconds { get; init; } = 5;
    public string GenerationVersion { get; init; } = "walkthrough-v1";
    public int RequestTimeoutSeconds { get; init; } = 120;
    public int MaxOutputMegabytes { get; init; } = 100;
    public decimal EstimatedCostPerCallUsd { get; init; }
}
