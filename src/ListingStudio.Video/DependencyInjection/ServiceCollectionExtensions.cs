using ListingStudio.Video.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ListingStudio.Video.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddVideo(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AIVideoOptions>().Bind(configuration.GetSection(AIVideoOptions.SectionName));
        return services;
    }
}
