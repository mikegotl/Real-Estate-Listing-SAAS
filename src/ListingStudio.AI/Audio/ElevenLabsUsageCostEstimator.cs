using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ListingStudio.AI.Audio;

public static class ElevenLabsUsageCostEstimator
{
    private const string CharacterCostHeader = "character-cost";

    public static async ValueTask<decimal> EstimateAsync(
        HttpRequestMessage request,
        HttpResponseMessage? response,
        decimal costPerThousandCharactersUsd,
        CancellationToken cancellationToken = default)
    {
        if (response?.IsSuccessStatusCode != true || costPerThousandCharactersUsd <= 0)
        {
            return 0;
        }

        var characterCount = ReadProviderCharacterCost(response)
            ?? await ReadRequestCharacterCountAsync(request, cancellationToken);
        return decimal.Round(
            characterCount * costPerThousandCharactersUsd / 1_000m,
            8,
            MidpointRounding.AwayFromZero);
    }

    private static decimal? ReadProviderCharacterCost(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues(CharacterCostHeader, out var values))
        {
            return null;
        }

        var value = values.FirstOrDefault();
        return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var cost)
            && cost >= 0
                ? cost
                : null;
    }

    private static async ValueTask<decimal> ReadRequestCharacterCountAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Content is null)
        {
            return 0;
        }

        try
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("text", out var textElement)
                && textElement.ValueKind == JsonValueKind.String
                    ? textElement.GetString()?.EnumerateRunes().Count() ?? 0
                    : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }
}
