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
using ListingStudio.Application.Campaigns;
using Microsoft.Extensions.Options;

namespace ListingStudio.Video.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddVideo(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AIVideoOptions>().Bind(configuration.GetSection(AIVideoOptions.SectionName));
        services.AddOptions<HiggsfieldOptions>().Bind(configuration.GetSection(HiggsfieldOptions.SectionName));
        services.AddOptions<HeyGenOptions>()
            .Bind(configuration.GetSection(HeyGenOptions.SectionName))
            .Validate(options => !options.Enabled
                || (Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri)
                    && uri.Scheme == Uri.UriSchemeHttps
                    && !string.IsNullOrWhiteSpace(options.ApiKey)
                    && !string.IsNullOrWhiteSpace(options.Model)
                    && options.Resolution == "720p"),
                "Enabled HeyGen presenter generation requires an HTTPS URL, API key, model, and 720p output.")
            .Validate(options => options.IntroMaximumSeconds is >= 1 and <= 10
                    && options.OutroMaximumSeconds is >= 0 and <= 10
                    && options.IntroMaximumSeconds + options.OutroMaximumSeconds <= 15
                    && options.PollIntervalSeconds is >= 1 and <= 60
                    && options.RequestTimeoutSeconds is >= 60 and <= 3_600
                    && options.MaxOutputMegabytes is >= 1 and <= 500
                    && options.EstimatedCostPerMinuteUsd is >= 0 and <= 100,
                "HeyGen presenter cost and processing limits are outside supported bounds.");
        services.AddOptions<FfmpegOptions>()
            .Bind(configuration.GetSection(FfmpegOptions.SectionName))
            .Validate(options => options.EnhancementTimeoutSeconds is >= 60 and <= 7_200,
                "FFmpeg:EnhancementTimeoutSeconds must be between 60 and 7200 seconds.");
        services.AddOptions<VideoBrandingTemplateOptions>()
            .Bind(configuration.GetSection(VideoBrandingTemplateOptions.SectionName));
        services.AddSingleton<IVideoRenderer, FfmpegVideoRenderer>();
        services.AddSingleton<IRenderedVideoValidator, FfmpegMp4TechnicalValidator>();
        services.AddSingleton<ICampaignSoundtrackService, OriginalCampaignSoundtrackService>();
        services.AddSingleton<IPropertyVideoTranscoder, FfmpegPropertyVideoTranscoder>();
        services.AddSingleton<IPropertyVideoFrameExtractor, FfmpegPropertyVideoFrameExtractor>();
        services.AddSingleton<IPropertyVideoClipFinder, FfmpegPropertyVideoClipFinder>();
        services.AddSingleton<IPhotoImageCodec, FfmpegPhotoImageCodec>();
        services.AddSingleton<IAerialIntroVideoEditor, FfmpegAerialIntroVideoEditor>();
        services.AddSingleton<IAgentAvatarPreviewRenderer, FfmpegAgentAvatarPreviewRenderer>();
        services.AddSingleton<IAgentAvatarNarrationExcerptRenderer, FfmpegAgentAvatarNarrationExcerptRenderer>();
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
        services.AddHttpClient<IAgentAvatarVideoProvider, HeyGenAgentAvatarVideoProvider>(client =>
            client.Timeout = Timeout.InfiniteTimeSpan)
            .AddHttpMessageHandler(provider =>
            {
                var video = provider.GetRequiredService<IOptions<HeyGenOptions>>().Value;
                var maximumEstimatedCost = Math.Round(
                    (video.IntroMaximumSeconds + video.OutroMaximumSeconds) / 60m
                        * video.EstimatedCostPerMinuteUsd,
                    6,
                    MidpointRounding.AwayFromZero);
                return new AiUsageHttpMessageHandler(
                    provider.GetRequiredService<IAiUsageRecorder>(),
                    "HeyGen",
                    video.Model,
                    "agent-avatar-video",
                    maximumEstimatedCost,
                    request => request.Method == HttpMethod.Post
                        && string.Equals(request.RequestUri?.AbsolutePath, "/v3/videos", StringComparison.OrdinalIgnoreCase));
            });
        return services;
    }
}
