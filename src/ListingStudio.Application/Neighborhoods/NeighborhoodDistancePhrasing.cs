namespace ListingStudio.Application.Neighborhoods;

/// <summary>
/// Speakable distance wording for marketing copy. Exact decimals read awkwardly in a voiceover, so distances are
/// rounded half up to the nearest whole mile and anything under one mile becomes "less than a mile away".
/// </summary>
public static class NeighborhoodDistancePhrasing
{
    public static int RoundToWholeMiles(decimal distanceMiles) =>
        (int)decimal.Round(distanceMiles, 0, MidpointRounding.AwayFromZero);

    public static string Describe(decimal distanceMiles)
    {
        if (distanceMiles < 1m)
        {
            return "less than a mile away";
        }

        var miles = RoundToWholeMiles(distanceMiles);
        return miles == 1 ? "about 1 mile away" : $"about {miles} miles away";
    }
}
