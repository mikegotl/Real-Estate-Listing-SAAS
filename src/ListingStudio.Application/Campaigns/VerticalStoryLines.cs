using System.Text.RegularExpressions;

namespace ListingStudio.Application.Campaigns;

/// <summary>
/// Splits the generated story's voiceover into short spoken lines, one sentence each, in walking order (the story
/// walks the storyboard stops in order, one paragraph per stop). Narrated library videos use these when the agent has
/// no approved narration of their own, so a format that speaks never goes quiet for lack of a script.
/// </summary>
public static partial class VerticalStoryLines
{
    public const string KeyPrefix = "story.voiceover.line.";

    public static IReadOnlyList<VerticalNarrationLine> From(string? voiceover)
    {
        if (string.IsNullOrWhiteSpace(voiceover))
        {
            return [];
        }

        var lines = new List<VerticalNarrationLine>();
        foreach (var paragraph in voiceover.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (Match sentence in SentencePattern().Matches(paragraph))
            {
                var text = sentence.Value.Trim();
                if (text.Length > 0)
                {
                    lines.Add(new VerticalNarrationLine($"{KeyPrefix}{lines.Count + 1}", text, null, FromAgent: false));
                }
            }
        }

        return lines;
    }

    /// <summary>A sentence ends at ., ! or ? followed by a space or the end; "2.5" and "St." mid-sentence stay whole.</summary>
    [GeneratedRegex(@"[^.!?]+(?:[.!?](?=\S)[^.!?]*)*[.!?]*", RegexOptions.CultureInvariant)]
    private static partial Regex SentencePattern();
}
