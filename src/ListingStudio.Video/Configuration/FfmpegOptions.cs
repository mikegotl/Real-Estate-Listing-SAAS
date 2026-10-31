namespace ListingStudio.Video.Configuration;

public sealed class FfmpegOptions
{
    public const string SectionName = "FFmpeg";

    public string ExecutablePath { get; init; } = "ffmpeg";

    public string ProbeExecutablePath { get; init; } = "ffprobe";

    public int RenderTimeoutSeconds { get; init; } = 600;

    public int EnhancementTimeoutSeconds { get; init; } = 1_800;
}
