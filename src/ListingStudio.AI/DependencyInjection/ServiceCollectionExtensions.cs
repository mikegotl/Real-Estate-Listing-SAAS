using ListingStudio.AI.Configuration;
using ListingStudio.AI.Properties;
using ListingStudio.Application.Properties;
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
        return services;
    }
}
