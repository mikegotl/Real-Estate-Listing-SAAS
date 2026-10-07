namespace ListingStudio.AI.Configuration;

public sealed class OpenAIOptions
{
    public const string SectionName = "OpenAI";
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string ResponsesEndpoint { get; init; } = "https://api.openai.com/v1/responses";
    public string SpeechEndpoint { get; init; } = "https://api.openai.com/v1/audio/speech";
}

public sealed class VoiceOptions
{
    public const string SectionName = "Voice";
    public string Provider { get; init; } = "OpenAI";
    public string ApiKey { get; init; } = string.Empty;
    public string VoiceId { get; init; } = string.Empty;
    public string Endpoint { get; init; } = "https://api.elevenlabs.io/v1/text-to-speech";
    public string ModelId { get; init; } = "eleven_multilingual_v2";
    public string OutputFormat { get; init; } = "mp3_44100_128";
    public string OpenAIModel { get; init; } = "gpt-4o-mini-tts";
    public string OpenAIVoice { get; init; } = "marin";
    public string OpenAIInstructions { get; init; } =
        "Speak with a warm, polished, trustworthy real-estate narration style at a natural pace.";
    public int MaxRetryAttempts { get; init; } = 3;
    public int RetryBaseDelayMilliseconds { get; init; } = 250;
}
