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

namespace ListingStudio.AI.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAI(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<OpenAIOptions>().Bind(configuration.GetSection(OpenAIOptions.SectionName));
        services.AddOptions<VoiceOptions>().Bind(configuration.GetSection(VoiceOptions.SectionName));
        services.AddHttpClient<IPropertyMediaAnalyzer, OpenAIPropertyMediaAnalyzer>();
        services.AddHttpClient<IPropertyStoryGenerator, OpenAIPropertyStoryGenerator>();
        services.AddHttpClient<IVideoDirector, OpenAIVideoDirector>();
        services.AddHttpClient<IVoiceProvider, ElevenLabsVoiceProvider>();
        return services;
    }
}
