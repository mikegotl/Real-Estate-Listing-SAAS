using ListingStudio.Video.Configuration;
using ListingStudio.Video.Rendering;
using ListingStudio.Application.Videos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ListingStudio.Video.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddVideo(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AIVideoOptions>().Bind(configuration.GetSection(AIVideoOptions.SectionName));
        services.AddOptions<FfmpegOptions>().Bind(configuration.GetSection(FfmpegOptions.SectionName));
        services.AddOptions<VideoBrandingTemplateOptions>()
            .Bind(configuration.GetSection(VideoBrandingTemplateOptions.SectionName));
        services.AddSingleton<IVideoRenderer, FfmpegVideoRenderer>();
        return services;
    }
}
