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
    /// <summary>The ElevenLabs voice used when no voice is configured for the selected accent.</summary>
    public string VoiceId { get; init; } = "EXAVITQu4vr4xnSDxMaL";

    /// <summary>
    /// ElevenLabs voice IDs by campaign voice accent (for example American, British, Castilian). The multilingual
    /// model speaks every campaign language, so an accent without its own voice uses <see cref="VoiceId"/>; add a
    /// native-speaker voice from the ElevenLabs library here for the best accent.
    /// </summary>
    public Dictionary<string, string> ElevenLabsVoices { get; init; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["American"] = "EXAVITQu4vr4xnSDxMaL",
        ["Canadian"] = "EXAVITQu4vr4xnSDxMaL",
        ["British"] = "Xb7hH8MSUJpSbSDYk0k2",
        ["Australian"] = "IKne3meq5aSn9XLyUdCD",
    };

    /// <summary>Lower is more expressive, higher is steadier. 0 to 1.</summary>
    public decimal Stability { get; init; } = 0.45m;

    public decimal SimilarityBoost { get; init; } = 0.8m;

    /// <summary>ElevenLabs recommends 0; raising it exaggerates the voice's style and adds latency.</summary>
    public decimal Style { get; init; }

    /// <summary>Speaking rate, 0.7 to 1.2.</summary>
    public decimal Speed { get; init; } = 1.0m;

    public bool UseSpeakerBoost { get; init; } = true;

    /// <summary>The longest text sent in one request; longer scripts are split at segment or sentence breaks.</summary>
    public int MaximumCharactersPerRequest { get; init; } = 9_000;
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

    /// <summary>Off until the owner approves paid identity-preserving edits for agent-owned photos.</summary>
    public bool AgentPortraitEnhancementEnabled { get; init; }

    public string ImageModel { get; init; } = "gpt-image-2.5-flare";

    public string ImageEditsEndpoint { get; init; } = "https://api.openai.com/v1/images/edits";

    public string Quality { get; init; } = "high";

    /// <summary>Legacy opt-in for models that accept input_fidelity; GPT Image 2 and 2.5 always omit it.</summary>
    public string InputFidelity { get; init; } = string.Empty;

    public int RequestTimeoutSeconds { get; init; } = 240;

    public int MaxOutputMegabytes { get; init; } = 40;
    public decimal EstimatedCostUsd { get; init; }

    public decimal AgentPortraitEstimatedCostUsd { get; init; }
}
