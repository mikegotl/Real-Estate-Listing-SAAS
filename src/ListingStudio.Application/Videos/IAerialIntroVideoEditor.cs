namespace ListingStudio.Application.Videos;

/// <summary>
/// Turns a generated pull-back into the drone intro: plays it backwards so it ends on the real photo, and burns in
/// the AI disclosure so it travels with the clip into every video that uses it.
/// </summary>
public interface IAerialIntroVideoEditor
{
    Task<AerialIntroEditResult> PrepareAsync(Stream generatedClip, CancellationToken cancellationToken = default);
}

public sealed record AerialIntroEditResult(Stream Content, long FileSize, int DurationMs);
