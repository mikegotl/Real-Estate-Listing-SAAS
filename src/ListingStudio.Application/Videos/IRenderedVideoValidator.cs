using ListingStudio.Domain.Videos;

namespace ListingStudio.Application.Videos;

/// <summary>
/// The technical check every rendered campaign MP4 must pass before it is stored: the file plays from start to finish
/// with the expected codecs, frame size and length. It says nothing about the content; a technically valid video
/// still needs a person to check its facts, pronunciation, framing, captions, sound and contact details.
/// </summary>
public interface IRenderedVideoValidator
{
    Task<RenderedVideoValidation> ValidateAsync(
        string path,
        Mp4Expectations expectations,
        CancellationToken cancellationToken = default);
}

public sealed record Mp4Expectations(
    int Width,
    int Height,
    string VideoCodec,
    string AudioCodec,
    double DurationSeconds,
    double DurationToleranceSeconds)
{
    /// <summary>What a render of this specification must produce.</summary>
    public static Mp4Expectations For(VideoProductionSpecification specification)
    {
        ArgumentNullException.ThrowIfNull(specification);
        // The renderer trims every program to its requested length (long-form videos store theirs in seconds).
        var seconds = (double)(int)specification.RequestedDuration;
        return new Mp4Expectations(
            specification.Output.Width,
            specification.Output.Height,
            specification.Output.VideoCodec,
            specification.Output.AudioCodec,
            seconds,
            Math.Max(0.35, seconds * 0.01));
    }
}

public sealed record RenderedVideoValidation(
    bool IsValid,
    IReadOnlyList<string> Problems,
    int? Width,
    int? Height,
    string? VideoCodec,
    string? AudioCodec,
    double? DurationSeconds,
    bool Decoded)
{
    /// <summary>A short summary safe to log or show: no paths, property content or URLs.</summary>
    public string Summary => IsValid
        ? $"Passed: {Width}x{Height} {VideoCodec}/{AudioCodec}, {DurationSeconds:0.00}s, full decode."
        : $"Failed: {string.Join(" ", Problems)}";
}

/// <summary>A rendered video that did not pass the technical check; the job retries the render.</summary>
public sealed class RenderedVideoValidationException(RenderedVideoValidation validation)
    : Exception($"The rendered video failed its technical check. {validation.Summary}")
{
    public RenderedVideoValidation Validation { get; } = validation;
}
