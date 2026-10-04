namespace ListingStudio.AI.Configuration;

public sealed class OpenAIOptions
{
    public const string SectionName = "OpenAI";
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string ResponsesEndpoint { get; init; } = "https://api.openai.com/v1/responses";
}

public sealed class VoiceOptions
{
    public const string SectionName = "Voice";
    public string Provider { get; init; } = string.Empty;
    public string ApiKey { get; init; } = string.Empty;
    public string VoiceId { get; init; } = string.Empty;
}
