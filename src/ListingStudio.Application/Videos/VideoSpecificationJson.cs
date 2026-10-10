using System.Text.Json;
using System.Text.Json.Serialization;
using ListingStudio.Domain.Videos;

namespace ListingStudio.Application.Videos;

public static class VideoSpecificationJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true,
        };
        options.Converters.Add(new RequestedDurationConverter());
        options.Converters.Add(new AspectRatioConverter());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    private sealed class RequestedDurationConverter : JsonConverter<RequestedDuration>
    {
        public override RequestedDuration Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetInt32();
            return value is >= 15 and <= 900
                ? (RequestedDuration)value
                : throw new JsonException("Requested duration is not supported.");
        }

        public override void Write(Utf8JsonWriter writer, RequestedDuration value, JsonSerializerOptions options) =>
            writer.WriteNumberValue((int)value);
    }

    private sealed class AspectRatioConverter : JsonConverter<VideoAspectRatio>
    {
        public override VideoAspectRatio Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString() switch
            {
                "16:9" => VideoAspectRatio.Landscape16By9,
                "9:16" => VideoAspectRatio.Vertical9By16,
                _ => throw new JsonException("Aspect ratio is not supported."),
            };

        public override void Write(Utf8JsonWriter writer, VideoAspectRatio value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value switch
            {
                VideoAspectRatio.Landscape16By9 => "16:9",
                VideoAspectRatio.Vertical9By16 => "9:16",
                _ => throw new JsonException("Aspect ratio is not supported."),
            });
    }
}
