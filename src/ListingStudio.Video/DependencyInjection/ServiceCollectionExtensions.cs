using ListingStudio.Video.Configuration;
using ListingStudio.Video.Rendering;
using ListingStudio.Application.Videos;
using ListingStudio.Video.Generation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ListingStudio.Video.Health;
using ListingStudio.Application.Properties;
using ListingStudio.Video.Processing;
using ListingStudio.Application.Administration;
using Microsoft.Extensions.Options;

namespace ListingStudio.Video.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddVideo(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AIVideoOptions>().Bind(configuration.GetSection(AIVideoOptions.SectionName));
        services.AddOptions<HiggsfieldOptions>().Bind(configuration.GetSection(HiggsfieldOptions.SectionName));
        services.AddOptions<FfmpegOptions>()
            .Bind(configuration.GetSection(FfmpegOptions.SectionName))
            .Validate(options => options.EnhancementTimeoutSeconds is >= 60 and <= 7_200,
                "FFmpeg:EnhancementTimeoutSeconds must be between 60 and 7200 seconds.");
        services.AddOptions<VideoBrandingTemplateOptions>()
            .Bind(configuration.GetSection(VideoBrandingTemplateOptions.SectionName));
        services.AddSingleton<IVideoRenderer, FfmpegVideoRenderer>();
        services.AddSingleton<ICampaignSoundtrackService, OriginalCampaignSoundtrackService>();
        services.AddSingleton<IPropertyVideoTranscoder, FfmpegPropertyVideoTranscoder>();
        services.AddSingleton<IPropertyVideoFrameExtractor, FfmpegPropertyVideoFrameExtractor>();
        services.AddSingleton<IPhotoImageCodec, FfmpegPhotoImageCodec>();
        services.AddHealthChecks()
            .AddCheck<FfmpegHealthCheck>("ffmpeg", tags: ["ready"]);
        services.AddHttpClient<IAiVideoProvider, HttpAiVideoProvider>(client =>
            client.Timeout = Timeout.InfiniteTimeSpan)
            .AddHttpMessageHandler(provider =>
            {
                var video = provider.GetRequiredService<IOptions<AIVideoOptions>>().Value;
                return new AiUsageHttpMessageHandler(
                    provider.GetRequiredService<IAiUsageRecorder>(),
                    string.IsNullOrWhiteSpace(video.Provider) ? "AI video" : video.Provider,
                    string.IsNullOrWhiteSpace(video.Model) ? "unconfigured" : video.Model,
                    "generated-video",
                    video.EstimatedCostUsd);
            });
        services.AddHttpClient<IWalkthroughVideoProvider, HiggsfieldWalkthroughVideoProvider>(client =>
            client.Timeout = Timeout.InfiniteTimeSpan)
            .AddHttpMessageHandler(provider =>
            {
                var video = provider.GetRequiredService<IOptions<HiggsfieldOptions>>().Value;
                return new AiUsageHttpMessageHandler(
                    provider.GetRequiredService<IAiUsageRecorder>(),
                    "Higgsfield",
                    video.Model,
                    "walkthrough-video",
                    video.EstimatedCostPerCallUsd,
                    request => request.Method == HttpMethod.Post
                        && string.Equals(
                            request.RequestUri?.AbsolutePath.Trim('/'),
                            video.Model.Trim('/'),
                            StringComparison.OrdinalIgnoreCase));
            });
        return services;
    }
}
