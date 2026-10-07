namespace ListingStudio.Worker;

public sealed class VideoProcessingWorkerOptions
{
    public const string SectionName = "VideoProcessing";

    public bool Enabled { get; init; } = true;
    public int PollIntervalSeconds { get; init; } = 3;
}
