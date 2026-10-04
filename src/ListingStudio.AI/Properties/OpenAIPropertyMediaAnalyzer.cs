using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ListingStudio.AI.Configuration;
using ListingStudio.Application.Properties;
using ListingStudio.Domain.Properties;
using Microsoft.Extensions.Options;

namespace ListingStudio.AI.Properties;

public sealed class OpenAIPropertyMediaAnalyzer(
    HttpClient httpClient,
    IOptions<OpenAIOptions> options) : IPropertyMediaAnalyzer
{
    private const string Instructions = """
        You are analyzing professional real-estate listing photography.

        Classify the supplied photograph.

        Determine:
        - room/property category
        - brief factual visual description
        - image quality from 0-100
        - hero-image suitability from 0-100
        - whether the image is interior or exterior
        - whether people are visible
        - potential visual problems
        - recommended position within a property-tour sequence

        Do not infer property features that are not clearly visible.

        Do not infer:
        - location
        - price
        - school district
        - neighborhood quality
        - materials unless visually certain
        - room dimensions
        - property condition beyond visible evidence

        Return only the requested structured schema.
        """;

    private static readonly JsonElement OutputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "category": {
              "type": "string",
              "enum": [
                "FrontExterior", "RearExterior", "Aerial", "Entry", "LivingRoom", "Kitchen",
                "DiningRoom", "PrimaryBedroom", "Bedroom", "PrimaryBathroom", "Bathroom", "Office",
                "Laundry", "Garage", "Pool", "Patio", "Backyard", "Community", "Other"
              ]
            },
            "roomType": { "type": "string" },
            "qualityScore": { "type": "integer", "minimum": 0, "maximum": 100 },
            "heroScore": { "type": "integer", "minimum": 0, "maximum": 100 },
            "isExterior": { "type": "boolean" },
            "isInterior": { "type": "boolean" },
            "containsPeople": { "type": "boolean" },
            "potentialProblems": { "type": "array", "items": { "type": "string" } },
            "description": { "type": "string" },
            "suggestedDisplayOrder": { "type": "integer", "minimum": 0, "maximum": 49 }
          },
          "required": [
            "category", "roomType", "qualityScore", "heroScore", "isExterior", "isInterior",
            "containsPeople", "potentialProblems", "description", "suggestedDisplayOrder"
          ]
        }
        """).RootElement.Clone();

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<PropertyMediaAnalysis> AnalyzeAsync(
        PropertyMediaAnalysisInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Content);
        var configuration = options.Value;
        if (string.IsNullOrWhiteSpace(configuration.ApiKey))
        {
            throw new InvalidOperationException("OpenAI media analysis requires an API key.");
        }

        if (string.IsNullOrWhiteSpace(configuration.Model))
        {
            throw new InvalidOperationException("OpenAI media analysis requires a model name.");
        }

        if (!Uri.TryCreate(configuration.ResponsesEndpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("OpenAI:ResponsesEndpoint must be an absolute HTTPS URL.");
        }

        using var imageBuffer = new MemoryStream();
        await input.Content.CopyToAsync(imageBuffer, cancellationToken);
        var imageUrl = $"data:{input.MimeType};base64,{Convert.ToBase64String(imageBuffer.GetBuffer().AsSpan(0, checked((int)imageBuffer.Length)))}";
        var payload = new
        {
            model = configuration.Model,
            store = false,
            max_output_tokens = 1_500,
            instructions = Instructions,
            input = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "input_text", text = "Analyze this listing photograph." },
                        new { type = "input_image", image_url = imageUrl, detail = "high" },
                    },
                },
            },
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    name = "property_media_analysis",
                    strict = true,
                    schema = OutputSchema,
                },
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration.ApiKey);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var requestId = response.Headers.TryGetValues("x-request-id", out var values)
                ? values.FirstOrDefault()
                : null;
            throw new HttpRequestException(
                requestId is null
                    ? $"OpenAI media analysis failed with HTTP {(int)response.StatusCode}."
                    : $"OpenAI media analysis failed with HTTP {(int)response.StatusCode} (request {requestId}).",
                null,
                response.StatusCode);
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var responseJson = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
        var outputText = GetOutputText(responseJson.RootElement);
        var result = JsonSerializer.Deserialize<StructuredAnalysis>(outputText, SerializerOptions)
            ?? throw new InvalidDataException("OpenAI returned an empty property media analysis.");

        return new PropertyMediaAnalysis(
            result.Category,
            result.RoomType,
            result.QualityScore,
            result.HeroScore,
            result.IsExterior,
            result.IsInterior,
            result.ContainsPeople,
            result.PotentialProblems,
            result.Description,
            result.SuggestedDisplayOrder);
    }

    private static string GetOutputText(JsonElement response)
    {
        if (!response.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("OpenAI returned no property media analysis output.");
        }

        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var part in content.EnumerateArray())
            {
                var type = part.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
                if (type == "refusal")
                {
                    throw new InvalidOperationException("OpenAI refused the property media analysis request.");
                }

                if (type == "output_text"
                    && part.TryGetProperty("text", out var text)
                    && !string.IsNullOrWhiteSpace(text.GetString()))
                {
                    return text.GetString()!;
                }
            }
        }

        throw new InvalidDataException("OpenAI returned no structured property media analysis.");
    }

    private sealed record StructuredAnalysis(
        PropertyMediaCategory Category,
        string RoomType,
        int QualityScore,
        int HeroScore,
        bool IsExterior,
        bool IsInterior,
        bool ContainsPeople,
        string[] PotentialProblems,
        string Description,
        int SuggestedDisplayOrder);
}
