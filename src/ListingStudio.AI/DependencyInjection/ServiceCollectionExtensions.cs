using ListingStudio.AI.Configuration;
using ListingStudio.AI.Properties;
using ListingStudio.Application.Properties;
using ListingStudio.Application.Stories;
using ListingStudio.AI.Stories;
using ListingStudio.Application.Videos;
using ListingStudio.AI.Videos;
using ListingStudio.Application.Audio;
using ListingStudio.AI.Audio;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ListingStudio.AI.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAI(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<OpenAIOptions>().Bind(configuration.GetSection(OpenAIOptions.SectionName));
        services.AddOptions<VoiceOptions>().Bind(configuration.GetSection(VoiceOptions.SectionName));
        services.AddOptions<PhotoFixOptions>()
            .Bind(configuration.GetSection(PhotoFixOptions.SectionName))
            .Validate(options => options.RequestTimeoutSeconds is >= 30 and <= 900,
                "PhotoFixes:RequestTimeoutSeconds must be between 30 and 900 seconds.")
            .Validate(options => options.MaxOutputMegabytes is >= 1 and <= 100,
                "PhotoFixes:MaxOutputMegabytes must be between 1 and 100.");
        services.AddHttpClient<IPropertyMediaAnalyzer, OpenAIPropertyMediaAnalyzer>();
        services.AddHttpClient<IPhotoClutterRemover, OpenAIPhotoClutterRemover>(client =>
            client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddHttpClient<IPropertyStoryGenerator, OpenAIPropertyStoryGenerator>();
        services.AddHttpClient<IVideoDirector, OpenAIVideoDirector>();
        services.AddHttpClient<ElevenLabsVoiceProvider>();
        services.AddHttpClient<OpenAIVoiceProvider>();
        services.AddScoped<IVoiceProvider>(provider =>
        {
            var voice = provider.GetRequiredService<IOptions<VoiceOptions>>().Value;
            return voice.Provider.ToUpperInvariant() switch
            {
                "OPENAI" => provider.GetRequiredService<OpenAIVoiceProvider>(),
                "ELEVENLABS" => provider.GetRequiredService<ElevenLabsVoiceProvider>(),
                _ => throw new InvalidOperationException(
                    "Voice:Provider must be OpenAI or ElevenLabs for narration generation."),
            };
        });
        return services;
    }
}
