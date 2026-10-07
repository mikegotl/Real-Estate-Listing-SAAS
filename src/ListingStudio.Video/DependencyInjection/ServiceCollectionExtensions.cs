using ListingStudio.Video.Configuration;
using ListingStudio.Video.Rendering;
using ListingStudio.Application.Videos;
using ListingStudio.Video.Generation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ListingStudio.Video.Health;
using ListingStudio.Application.Properties;
using ListingStudio.Video.Processing;

namespace ListingStudio.Video.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddVideo(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AIVideoOptions>().Bind(configuration.GetSection(AIVideoOptions.SectionName));
        services.AddOptions<FfmpegOptions>()
            .Bind(configuration.GetSection(FfmpegOptions.SectionName))
            .Validate(options => options.EnhancementTimeoutSeconds is >= 60 and <= 7_200,
                "FFmpeg:EnhancementTimeoutSeconds must be between 60 and 7200 seconds.");
        services.AddOptions<VideoBrandingTemplateOptions>()
            .Bind(configuration.GetSection(VideoBrandingTemplateOptions.SectionName));
        services.AddSingleton<IVideoRenderer, FfmpegVideoRenderer>();
        services.AddSingleton<IPropertyVideoTranscoder, FfmpegPropertyVideoTranscoder>();
        services.AddHealthChecks()
            .AddCheck<FfmpegHealthCheck>("ffmpeg", tags: ["ready"]);
        services.AddHttpClient<IAiVideoProvider, HttpAiVideoProvider>(client =>
            client.Timeout = Timeout.InfiniteTimeSpan);
        return services;
    }
}
