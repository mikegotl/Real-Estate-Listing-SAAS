namespace ListingStudio.Application.Properties;

public static class NarrationScriptPolicy
{
    private const int WordsPerMinute = 145;
    private const int OpeningAndClosingSeconds = 6;

    public static int CountWords(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    public static bool RequiresLongForm(string text) =>
        CountWords(text) > IPropertyNarrationScriptService.ShortFormWordLimit;

    public static int EstimateLongFormDurationSeconds(string text)
    {
        var words = CountWords(text);
        if (words <= IPropertyNarrationScriptService.ShortFormWordLimit)
        {
            return (int)Domain.Videos.RequestedDuration.Hero60;
        }

        var narrationSeconds = (int)Math.Ceiling(words * 60m / WordsPerMinute);
        return Math.Min(
            IPropertyNarrationScriptService.MaximumLongFormDurationSeconds,
            narrationSeconds + OpeningAndClosingSeconds);
    }
}
