namespace ListingStudio.Application.Properties;

/// <summary>
/// Finds the stretches of a property video that make good short-form shots: steady, sharp, well lit and full of
/// detail in the part of the frame a vertical video keeps.
/// </summary>
public interface IPropertyVideoClipFinder
{
    Task<IReadOnlyList<VideoClipSpan>> FindClipsAsync(
        Stream content,
        string originalFilename,
        CancellationToken cancellationToken = default);
}

/// <summary>A usable stretch of video from <paramref name="StartMs"/> to <paramref name="EndMs"/>; higher scores look better.</summary>
public sealed record VideoClipSpan(int StartMs, int EndMs, double Score);
