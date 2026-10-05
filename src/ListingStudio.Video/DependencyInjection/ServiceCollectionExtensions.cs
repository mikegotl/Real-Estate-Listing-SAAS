using ListingStudio.Video.Configuration;
using ListingStudio.Video.Rendering;
using ListingStudio.Application.Videos;
using ListingStudio.Video.Generation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ListingStudio.Video.Health;

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
        services.AddHealthChecks()
            .AddCheck<FfmpegHealthCheck>("ffmpeg", tags: ["ready"]);
        services.AddHttpClient<IAiVideoProvider, HttpAiVideoProvider>(client =>
            client.Timeout = Timeout.InfiniteTimeSpan);
        return services;
    }
}
