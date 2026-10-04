using System.Globalization;
using System.Text.RegularExpressions;
using ListingStudio.Domain.Stories;

namespace ListingStudio.Application.Stories;

public sealed partial class PropertyStoryGroundingValidator : IPropertyStoryGroundingValidator
{
    private static readonly string[] ProtectedClaims =
    [
        "school", "district", "zoned for", "hoa", "homeowners association", "hoa dues",
        "neighborhood", "walkable", "safe area", "quiet area", "minutes from", "steps from", "close to",
        "investment", "appreciation", "rental income", "low taxes", "financing", "mortgage",
        "renovated", "remodeled", "upgraded", "new roof", "granite", "quartz", "hardwood", "marble",
        "stainless steel", "solar", "energy efficient", "smart home", "ocean view", "mountain view",
        "water view", "waterfront", "pool", "garage", "fireplace",
    ];

    private static readonly IReadOnlyDictionary<string, decimal> NumberWords =
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            ["zero"] = 0,
            ["one"] = 1,
            ["two"] = 2,
            ["three"] = 3,
            ["four"] = 4,
            ["five"] = 5,
            ["six"] = 6,
            ["seven"] = 7,
            ["eight"] = 8,
            ["nine"] = 9,
            ["ten"] = 10,
            ["eleven"] = 11,
            ["twelve"] = 12,
        };

    public PropertyStoryGroundingResult Validate(
        PropertyStoryGenerationRequest request,
        PropertyStoryContent content)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(content);

        var text = string.Join(
            '\n',
            new[]
            {
                content.CampaignTitle,
                content.OpeningHook,
                content.PropertyNarrative,
                content.VoiceoverScript,
                content.ClosingCta,
                content.SocialCaptionLong,
                content.SocialCaptionShort,
            }.Concat(content.Highlights ?? []));

        var errors = new List<string>();
        ValidateRequiredContent(content, errors);
        ValidateNumbers(request, text, errors);
        ValidateProtectedClaims(request.VerifiedProperty.Description, text, errors);

        return errors.Count == 0
            ? PropertyStoryGroundingResult.Success
            : new PropertyStoryGroundingResult(false, errors);
    }

    private static void ValidateRequiredContent(PropertyStoryContent content, List<string> errors)
    {
        var fields = new Dictionary<string, (string? Value, int MaximumLength)>
        {
            [nameof(content.CampaignTitle)] = (content.CampaignTitle, 200),
            [nameof(content.OpeningHook)] = (content.OpeningHook, 500),
            [nameof(content.PropertyNarrative)] = (content.PropertyNarrative, 4_000),
            [nameof(content.VoiceoverScript)] = (content.VoiceoverScript, 6_000),
            [nameof(content.ClosingCta)] = (content.ClosingCta, 500),
            [nameof(content.SocialCaptionLong)] = (content.SocialCaptionLong, 2_200),
            [nameof(content.SocialCaptionShort)] = (content.SocialCaptionShort, 500),
        };

        foreach (var (name, field) in fields)
        {
            if (string.IsNullOrWhiteSpace(field.Value))
            {
                errors.Add($"{name} is required.");
            }
            else if (field.Value.Length > field.MaximumLength)
            {
                errors.Add($"{name} exceeds {field.MaximumLength} characters.");
            }
        }

        if (content.Highlights is null || content.Highlights.Count is < 1 or > 12)
        {
            errors.Add("Between 1 and 12 highlights are required.");
        }
        else if (content.Highlights.Any(highlight => string.IsNullOrWhiteSpace(highlight) || highlight.Length > 500))
        {
            errors.Add("Each highlight must contain no more than 500 characters.");
        }
    }

    private static void ValidateNumbers(
        PropertyStoryGenerationRequest request,
        string generatedText,
        List<string> errors)
    {
        var property = request.VerifiedProperty;
        var allowed = new HashSet<decimal>
        {
            property.ListingPrice,
            property.Bedrooms,
            property.Bathrooms,
        };

        AddIfPresent(allowed, property.SquareFeet);
        AddIfPresent(allowed, property.LotSize);
        AddIfPresent(allowed, property.YearBuilt);

        AddNumbersFromText(allowed, property.Address1);
        AddNumbersFromText(allowed, property.Address2);
        AddNumbersFromText(allowed, property.ZipCode);
        AddNumbersFromText(allowed, request.Branding.OrganizationName);
        AddNumbersFromText(allowed, request.Branding.AgentName);

        foreach (Match match in NumericTokenRegex().Matches(generatedText))
        {
            if (TryParseNumber(match.Value, out var value) && !allowed.Contains(value))
            {
                errors.Add($"Unsupported numeric claim: {match.Value}.");
            }
        }

        foreach (var (word, value) in NumberWords)
        {
            if (Regex.IsMatch(generatedText, $@"\b{Regex.Escape(word)}\b", RegexOptions.IgnoreCase)
                && !allowed.Contains(value))
            {
                errors.Add($"Unsupported numeric claim: {word}.");
            }
        }
    }

    private static void ValidateProtectedClaims(
        string? verifiedDescription,
        string generatedText,
        List<string> errors)
    {
        foreach (var claim in ProtectedClaims)
        {
            if (generatedText.Contains(claim, StringComparison.OrdinalIgnoreCase)
                && !(verifiedDescription?.Contains(claim, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                errors.Add($"Unsupported protected claim: {claim}.");
            }
        }
    }

    private static void AddNumbersFromText(HashSet<decimal> values, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        foreach (Match match in NumericTokenRegex().Matches(text))
        {
            if (TryParseNumber(match.Value, out var value))
            {
                values.Add(value);
            }
        }
    }

    private static bool TryParseNumber(string token, out decimal value)
    {
        var normalized = token.Trim().TrimStart('$').TrimEnd('%').Replace(",", string.Empty, StringComparison.Ordinal);
        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    private static void AddIfPresent(HashSet<decimal> values, int? value)
    {
        if (value.HasValue)
        {
            values.Add(value.Value);
        }
    }

    private static void AddIfPresent(HashSet<decimal> values, decimal? value)
    {
        if (value.HasValue)
        {
            values.Add(value.Value);
        }
    }

    [GeneratedRegex(@"(?<![\p{L}\d])\$?\d[\d,]*(?:\.\d+)?%?", RegexOptions.CultureInvariant)]
    private static partial Regex NumericTokenRegex();
}
