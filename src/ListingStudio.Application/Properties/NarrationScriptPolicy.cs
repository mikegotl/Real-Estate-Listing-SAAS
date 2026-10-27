using System.Text.RegularExpressions;

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

    /// <summary>Splits a script into voiced passages of about 260 characters, breaking at sentence ends.</summary>
    public static List<string> SplitIntoSegments(string text)
    {
        const int targetLength = 260;
        var sentences = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(paragraph => Regex.Split(
                paragraph,
                @"(?<=[.!?])\s+"))
            .Where(sentence => !string.IsNullOrWhiteSpace(sentence))
            .Select(sentence => sentence.Trim())
            .ToArray();
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var sentence in sentences.SelectMany(value => SplitLongScriptSegment(value, targetLength)))
        {
            if (current.Length > 0 && current.Length + 1 + sentence.Length > targetLength)
            {
                result.Add(current.ToString());
                current.Clear();
            }

            if (current.Length > 0)
            {
                current.Append(' ');
            }

            current.Append(sentence);
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    private static IEnumerable<string> SplitLongScriptSegment(string value, int maximumLength)
    {
        var remaining = value.Trim();
        while (remaining.Length > maximumLength)
        {
            var split = remaining[..maximumLength].LastIndexOf(' ');
            if (split < maximumLength / 2)
            {
                split = maximumLength;
            }

            yield return remaining[..split].Trim();
            remaining = remaining[split..].TrimStart();
        }

        if (remaining.Length > 0)
        {
            yield return remaining;
        }
    }
}
