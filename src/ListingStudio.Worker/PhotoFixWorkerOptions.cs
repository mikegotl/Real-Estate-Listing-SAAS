namespace ListingStudio.Worker;

public sealed class PhotoFixWorkerOptions
{
    public const string SectionName = "PhotoFixes";

    public int PollIntervalSeconds { get; init; } = 3;
}
