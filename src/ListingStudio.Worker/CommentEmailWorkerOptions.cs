namespace ListingStudio.Worker;

public sealed class CommentEmailWorkerOptions
{
    public const string SectionName = "Email:Comments";

    public int PollIntervalSeconds { get; init; } = 20;
}
