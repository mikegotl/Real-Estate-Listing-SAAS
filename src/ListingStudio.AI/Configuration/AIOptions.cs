namespace ListingStudio.AI.Configuration;

public sealed class OpenAIOptions
{
    public const string SectionName = "OpenAI";
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string ResponsesEndpoint { get; init; } = "https://api.openai.com/v1/responses";
    public string SpeechEndpoint { get; init; } = "https://api.openai.com/v1/audio/speech";
    public decimal MediaAnalysisEstimatedCostUsd { get; init; }
    public decimal StoryEstimatedCostUsd { get; init; }
    public decimal VideoDirectionEstimatedCostUsd { get; init; }
    public decimal SpeechEstimatedCostUsd { get; init; }
    public decimal LocalizationEstimatedCostUsd { get; init; }
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
    public decimal EstimatedCostUsd { get; init; }
}

/// <summary>Photo fixes on the Media tab. Brightness and color always runs locally; Remove clutter is a paid AI edit.</summary>
public sealed class PhotoFixOptions
{
    public const string SectionName = "PhotoFixes";

    /// <summary>Off until switched on; it also needs OpenAI:ApiKey.</summary>
    public bool RemoveClutterEnabled { get; init; }

    public string ImageModel { get; init; } = "gpt-image-1";

    public string ImageEditsEndpoint { get; init; } = "https://api.openai.com/v1/images/edits";

    public string Quality { get; init; } = "high";

    /// <summary>Asks the model to keep the input's details; leave empty for a model that does not take it.</summary>
    public string InputFidelity { get; init; } = "high";

    public int RequestTimeoutSeconds { get; init; } = 240;

    public int MaxOutputMegabytes { get; init; } = 40;
    public decimal EstimatedCostUsd { get; init; }
}
