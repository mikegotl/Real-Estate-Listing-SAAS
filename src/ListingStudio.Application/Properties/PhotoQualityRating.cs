namespace ListingStudio.Application.Properties;

public enum PhotoQuality
{
    Retake,
    Ok,
    Great,
}

/// <summary>
/// Turns the analyzer's 0–100 photo quality score into the three levels agents act on: keep it, keep it if nothing
/// better exists, or shoot it again.
/// </summary>
public static class PhotoQualityRating
{
    public const int GreatFrom = 75;
    public const int OkFrom = 50;

    public static PhotoQuality Rate(int qualityScore) => qualityScore switch
    {
        >= GreatFrom => PhotoQuality.Great,
        >= OkFrom => PhotoQuality.Ok,
        _ => PhotoQuality.Retake,
    };

    public static string Describe(PhotoQuality quality) => quality switch
    {
        PhotoQuality.Great => "Great",
        PhotoQuality.Ok => "OK",
        _ => "Retake",
    };
}
