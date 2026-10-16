namespace ListingStudio.Application.Properties;

/// <summary>
/// Pulls still candidates out of a property video: one sharp frame for each stretch of the video that shows a
/// different view, ready for room recognition.
/// </summary>
public interface IPropertyVideoFrameExtractor
{
    Task<IReadOnlyList<VideoFrameCandidate>> ExtractCandidatesAsync(
        Stream content,
        string originalFilename,
        int maximumCandidates,
        CancellationToken cancellationToken = default);
}

/// <summary>A JPEG still taken at <paramref name="TimestampMs"/>; higher <paramref name="Sharpness"/> is crisper.</summary>
public sealed record VideoFrameCandidate(int TimestampMs, double Sharpness, byte[] JpegContent);
