namespace ListingStudio.Application.Stories;

// Thrown when a generated story fails the grounding check. The message is written for the agent and is shown as is on
// the Story and Campaign tabs, so it names each unconfirmed feature and says how to fix it.
public sealed class PropertyStoryGroundingException : InvalidOperationException
{
    // Messages that ask the agent to check the Description start with this, so pages can add an Overview tab link.
    public const string DescriptionFixPrefix = "Generate story stopped because the story mentions ";

    // Campaign jobs store at most 1,000 characters of error text.
    public const int MaximumMessageLength = 1_000;

    public PropertyStoryGroundingException(PropertyStoryGroundingResult result)
        : base(CreateMessage(result))
    {
        UnverifiedClaims = result.UnverifiedClaims ?? [];
    }

    public IReadOnlyList<PropertyStoryUnverifiedClaim> UnverifiedClaims { get; }

    public static bool AsksForDescriptionFix(string? message) =>
        message?.StartsWith(DescriptionFixPrefix, StringComparison.Ordinal) == true;

    public static string CreateMessage(PropertyStoryGroundingResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var claims = (result.UnverifiedClaims ?? [])
            .DistinctBy(claim => claim.Claim, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (claims.Length == 0)
        {
            return Limit(
                "Generate story stopped because the story included details that could not be checked against the property: "
                + string.Join(' ', result.Errors)
                + " Generate the story again to get a new version.");
        }

        var words = JoinWords(claims.Select(claim => claim.Claim));
        var plural = claims.Length > 1;
        var it = plural ? "them" : "it";
        var seen = claims.Where(claim => claim.SeenInPhotos).ToArray();
        var photoNote = seen.Length == claims.Length
            ? $"Photo analysis did see {(plural ? "them" : "one")}, but photos alone don't confirm a feature."
            : seen.Length > 0
                ? $"Photo analysis saw {JoinWords(seen.Select(claim => claim.Claim))}; nothing else confirms the rest."
                : $"Nothing in the photos or property details confirms {it}.";
        var message = DescriptionFixPrefix + words
            + $", but the property Description doesn't include {(plural ? "those words" : "that word")}. "
            + photoNote
            + $" If the home really has {it}, edit the Description on the Overview tab so it includes {words}, then generate the story again."
            + $" If not, leave the Description as is and generate again so the story is written without {it}.";
        return Limit(message);
    }

    private static string JoinWords(IEnumerable<string> claims)
    {
        var quoted = claims.Select(claim => $"\"{claim.ToLowerInvariant()}\"").ToArray();
        return quoted.Length switch
        {
            0 => string.Empty,
            1 => quoted[0],
            _ => string.Join(", ", quoted[..^1]) + " and " + quoted[^1],
        };
    }

    private static string Limit(string message) => message.Length <= MaximumMessageLength
        ? message
        : message[..(MaximumMessageLength - 1)] + "…";
}
