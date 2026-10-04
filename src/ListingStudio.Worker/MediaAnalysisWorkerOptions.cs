namespace ListingStudio.Worker;

public sealed class MediaAnalysisWorkerOptions
{
    public const string SectionName = "MediaAnalysis";

    public bool Enabled { get; init; }

    public int PollIntervalSeconds { get; init; } = 10;
}
