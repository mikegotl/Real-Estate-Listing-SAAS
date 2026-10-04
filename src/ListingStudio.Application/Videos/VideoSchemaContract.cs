using System.Text.Json;
using System.Text.Json.Nodes;

namespace ListingStudio.Application.Videos;

// Validates precisely the canonical contract's keyword subset. Fail closed if the schema evolves.
public static class VideoSchemaContract
{
    private static readonly JsonElement Schema = LoadSchema();
    public static JsonElement EditorialSchema { get; } = CreateEditorialSchema();

    public static IReadOnlyList<string> Validate(JsonElement value, bool editorial = false)
    {
        var errors = new List<string>();
        Visit(editorial ? EditorialSchema : Schema, value, "$", errors);
        return errors;
    }

    private static JsonElement LoadSchema()
    {
        using var stream = typeof(VideoSchemaContract).Assembly.GetManifestResourceStream("VideoProductionSchema")
            ?? throw new InvalidOperationException("Video specification schema is missing.");
        using var document = JsonDocument.Parse(stream);
        VerifySchema(document.RootElement);
        return document.RootElement.Clone();
    }

    private static void VerifySchema(JsonElement node)
    {
        foreach (var property in node.EnumerateObject())
        {
            if (property.Name is not ("$schema" or "$id" or "title" or "$defs" or "type" or "properties"
                or "additionalProperties" or "required" or "items" or "enum" or "anyOf" or "$ref"
                or "format" or "minimum" or "maximum" or "exclusiveMinimum"))
                throw new InvalidOperationException("Video schema uses an unsupported validation keyword.");
            if (property.Name is "properties" or "$defs")
                foreach (var child in property.Value.EnumerateObject()) VerifySchema(child.Value);
            if (property.Name == "items") VerifySchema(property.Value);
            if (property.Name == "anyOf") foreach (var child in property.Value.EnumerateArray()) VerifySchema(child);
        }
    }

    private static JsonElement CreateEditorialSchema()
    {
        var node = JsonNode.Parse(Schema.GetRawText())!.AsObject();
        var properties = node["properties"]!.AsObject();
        foreach (var key in properties.Select(p => p.Key).Where(k => k is not ("audio" or "scenes")).ToArray()) properties.Remove(key);
        node["required"] = new JsonArray("audio", "scenes");
        node.Remove("$schema"); node.Remove("$id"); node.Remove("title");
        return JsonSerializer.SerializeToElement(node);
    }

    private static void Visit(JsonElement schema, JsonElement value, string path, List<string> errors)
    {
        if (schema.TryGetProperty("$ref", out var reference))
        {
            Visit(Schema.GetProperty("$defs").GetProperty(reference.GetString()!.Split('/')[^1]), value, path, errors);
            return;
        }
        if (schema.TryGetProperty("anyOf", out var alternatives))
        {
            if (!alternatives.EnumerateArray().Any(alternative => { var trial = new List<string>(); Visit(alternative, value, path, trial); return trial.Count == 0; })) errors.Add($"{path}: value does not match nullable type.");
            return;
        }
        var type = schema.GetProperty("type").GetString();
        var matches = type switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "number" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out _),
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _),
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => throw new InvalidOperationException("Unsupported video schema type."),
        };
        if (!matches) { errors.Add($"{path}: expected {type}."); return; }
        if (schema.TryGetProperty("enum", out var allowed) && !allowed.EnumerateArray().Any(item => JsonElement.DeepEquals(item, value))) errors.Add($"{path}: unsupported value.");
        if (type == "object")
        {
            var properties = schema.GetProperty("properties");
            foreach (var key in schema.GetProperty("required").EnumerateArray()) if (!value.TryGetProperty(key.GetString()!, out _)) errors.Add($"{path}.{key.GetString()}: required.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!seen.Add(property.Name)) errors.Add($"{path}.{property.Name}: duplicate field.");
                if (!properties.TryGetProperty(property.Name, out var child)) errors.Add($"{path}.{property.Name}: unknown field.");
                else Visit(child, property.Value, $"{path}.{property.Name}", errors);
            }
        }
        if (type == "array")
        {
            var index = 0;
            foreach (var item in value.EnumerateArray()) Visit(schema.GetProperty("items"), item, $"{path}[{index++}]", errors);
        }
        if (type is "integer" or "number")
        {
            var number = value.GetDecimal();
            if (schema.TryGetProperty("minimum", out var minimum) && number < minimum.GetDecimal()) errors.Add($"{path}: below minimum.");
            if (schema.TryGetProperty("exclusiveMinimum", out minimum) && number <= minimum.GetDecimal()) errors.Add($"{path}: below exclusive minimum.");
            if (schema.TryGetProperty("maximum", out var maximum) && number > maximum.GetDecimal()) errors.Add($"{path}: above maximum.");
        }
        if (schema.TryGetProperty("format", out var format) && format.GetString() == "uuid" && !Guid.TryParse(value.GetString(), out _)) errors.Add($"{path}: invalid UUID.");
    }
}
