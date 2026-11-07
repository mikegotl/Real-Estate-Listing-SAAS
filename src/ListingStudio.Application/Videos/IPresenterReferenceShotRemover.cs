namespace ListingStudio.Application.Videos;

/// <summary>
/// Kling is sent the agent's headshot only as a reference for the presenter's face, but it sometimes shows that photo,
/// background and all, as a shot of its own. This finds those shots by comparing the video with the headshot and
/// covers each one by holding the next real shot, so the picture never shows the headshot. The audio and the video's
/// length are not changed, so the agent's speech stays in sync.
/// </summary>
public interface IPresenterReferenceShotRemover
{
    Task<PresenterReferenceShotRemoval> RemoveAsync(
        string videoPath,
        string referenceImagePath,
        string outputPath,
        CancellationToken cancellationToken = default);
}

/// <summary>A span of the video, in seconds, that showed the reference photo.</summary>
public sealed record PresenterReferenceShot(double StartSeconds, double EndSeconds);

/// <summary>
/// The result of a check. When <see cref="Shots"/> is empty nothing was written and the original video should be used.
/// </summary>
public sealed record PresenterReferenceShotRemoval(IReadOnlyList<PresenterReferenceShot> Shots)
{
    public static PresenterReferenceShotRemoval None { get; } = new([]);

    public bool Removed => Shots.Count > 0;
}
