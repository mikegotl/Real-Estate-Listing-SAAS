namespace ListingStudio.Application.Audio;

public interface IVoiceProvider
{
    string GenerationVersion { get; }

    Task<VoiceGenerationResult> GenerateAsync(
        VoiceGenerationRequest request,
        CancellationToken cancellationToken = default);
}
