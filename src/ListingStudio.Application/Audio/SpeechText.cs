using System.Text.RegularExpressions;

namespace ListingStudio.Application.Audio;

/// <summary>
/// Rewrites on-screen listing shorthand the way a person would say it, so the voice does not spell out "S-Q F-T" or
/// "F-L": state codes after a city become the state's name, "sq ft" becomes "square feet", "2.5 baths" becomes
/// "two and a half baths" and the bullet separators of a fact card become pauses. English only; the caption keeps
/// the original text.
/// </summary>
public static partial class SpeechText
{
    private static readonly Dictionary<string, string> States = new(StringComparer.Ordinal)
    {
        ["AL"] = "Alabama",
        ["AK"] = "Alaska",
        ["AZ"] = "Arizona",
        ["AR"] = "Arkansas",
        ["CA"] = "California",
        ["CO"] = "Colorado",
        ["CT"] = "Connecticut",
        ["DE"] = "Delaware",
        ["DC"] = "D.C.",
        ["FL"] = "Florida",
        ["GA"] = "Georgia",
        ["HI"] = "Hawaii",
        ["ID"] = "Idaho",
        ["IL"] = "Illinois",
        ["IN"] = "Indiana",
        ["IA"] = "Iowa",
        ["KS"] = "Kansas",
        ["KY"] = "Kentucky",
        ["LA"] = "Louisiana",
        ["ME"] = "Maine",
        ["MD"] = "Maryland",
        ["MA"] = "Massachusetts",
        ["MI"] = "Michigan",
        ["MN"] = "Minnesota",
        ["MS"] = "Mississippi",
        ["MO"] = "Missouri",
        ["MT"] = "Montana",
        ["NE"] = "Nebraska",
        ["NV"] = "Nevada",
        ["NH"] = "New Hampshire",
        ["NJ"] = "New Jersey",
        ["NM"] = "New Mexico",
        ["NY"] = "New York",
        ["NC"] = "North Carolina",
        ["ND"] = "North Dakota",
        ["OH"] = "Ohio",
        ["OK"] = "Oklahoma",
        ["OR"] = "Oregon",
        ["PA"] = "Pennsylvania",
        ["RI"] = "Rhode Island",
        ["SC"] = "South Carolina",
        ["SD"] = "South Dakota",
        ["TN"] = "Tennessee",
        ["TX"] = "Texas",
        ["UT"] = "Utah",
        ["VT"] = "Vermont",
        ["VA"] = "Virginia",
        ["WA"] = "Washington",
        ["WV"] = "West Virginia",
        ["WI"] = "Wisconsin",
        ["WY"] = "Wyoming",
        ["PR"] = "Puerto Rico",
    };

    public static string ForSpeech(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var spoken = BulletPattern().Replace(text, ", ");
        spoken = SquareFeetPattern().Replace(spoken, "square feet");
        spoken = HalfBathPattern().Replace(spoken, match =>
            $"{match.Groups["whole"].Value} and a half {match.Groups["unit"].Value}");
        spoken = StatePattern().Replace(spoken, match =>
            States.TryGetValue(match.Groups["code"].Value, out var name)
                ? $"{match.Groups["lead"].Value}{name}"
                : match.Value);
        return spoken;
    }

    [GeneratedRegex(@"\s*[•·|]\s*", RegexOptions.CultureInvariant)]
    private static partial Regex BulletPattern();

    [GeneratedRegex(@"\b(?:sq\.?\s*ft\.?|sqft)(?=\W|$)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SquareFeetPattern();

    [GeneratedRegex(@"\b(?<whole>\d+)\.5\s+(?<unit>baths?|bathrooms?)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex HalfBathPattern();

    /// <summary>A two-letter capital code right after "City, ", at a word end.</summary>
    [GeneratedRegex(@"(?<lead>[A-Za-z.]+,\s*)(?<code>[A-Z]{2})(?![A-Za-z])", RegexOptions.CultureInvariant)]
    private static partial Regex StatePattern();
}
