namespace ListingStudio.Video.Configuration;

/// <summary>How Kling presenter videos are checked for shots of the agent's reference headshot.</summary>
public sealed class PresenterReferenceShotOptions
{
    public const string SectionName = "PresenterReferenceShots";

    public bool Enabled { get; init; } = true;

    /// <summary>Frames sampled per second for the comparison.</summary>
    public int SampleFramesPerSecond { get; init; } = 10;

    /// <summary>SSIM (0 to 1) on small blurred greyscale copies at or above which a frame counts as the headshot.</summary>
    public double MatchThreshold { get; init; } = 0.6;

    /// <summary>The opening seconds are never covered: the agent speaks on camera there.</summary>
    public double IgnoreFirstSeconds { get; init; } = 1.0;

    /// <summary>Matches closer together than this are treated as one shot.</summary>
    public double MergeGapSeconds { get; init; } = 0.3;

    /// <summary>A match shorter than this is ignored as a passing resemblance.</summary>
    public double MinimumShotSeconds { get; init; } = 0.2;

    /// <summary>Covered before a matched shot, for the cut into it.</summary>
    public double PaddingBeforeSeconds { get; init; } = 0.1;

    /// <summary>Covered after a matched shot: Kling tends to pan out of the photo into the next keyframe.</summary>
    public double PaddingAfterSeconds { get; init; } = 0.4;
}
