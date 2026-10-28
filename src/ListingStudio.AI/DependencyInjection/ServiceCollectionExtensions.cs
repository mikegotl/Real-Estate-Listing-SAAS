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
using ListingStudio.Application.Administration;
using ListingStudio.Application.Campaigns;
using ListingStudio.AI.Campaigns;
using ListingStudio.Application.Help;
using ListingStudio.AI.Help;

namespace ListingStudio.AI.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAI(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<OpenAIOptions>().Bind(configuration.GetSection(OpenAIOptions.SectionName));
        services.AddOptions<VoiceOptions>()
            .Bind(configuration.GetSection(VoiceOptions.SectionName))
            .Validate(options => options.EstimatedCostPerThousandCharactersUsd is >= 0 and <= 100,
                "Voice:EstimatedCostPerThousandCharactersUsd must be between 0 and 100.");
        services.AddOptions<PhotoFixOptions>()
            .Bind(configuration.GetSection(PhotoFixOptions.SectionName))
            .Validate(options => options.RequestTimeoutSeconds is >= 30 and <= 900,
                "PhotoFixes:RequestTimeoutSeconds must be between 30 and 900 seconds.")
            .Validate(options => options.MaxOutputMegabytes is >= 1 and <= 100,
                "PhotoFixes:MaxOutputMegabytes must be between 1 and 100.");
        services.AddHttpClient<IPropertyMediaAnalyzer, OpenAIPropertyMediaAnalyzer>()
            .AddHttpMessageHandler(provider => OpenAiHandler(
                provider, "property-media-analysis", options => options.MediaAnalysisEstimatedCostUsd));
        services.AddHttpClient<IPhotoClutterRemover, OpenAIPhotoClutterRemover>(client =>
            client.Timeout = Timeout.InfiniteTimeSpan)
            .AddHttpMessageHandler(provider =>
            {
                var photo = provider.GetRequiredService<IOptions<PhotoFixOptions>>().Value;
                return new AiUsageHttpMessageHandler(
                    provider.GetRequiredService<IAiUsageRecorder>(),
                    "OpenAI",
                    photo.ImageModel,
                    "photo-clutter-removal",
                    photo.EstimatedCostUsd);
            });
        services.AddHttpClient<IAgentPortraitEnhancer, OpenAIAgentPortraitEnhancer>(client =>
            client.Timeout = Timeout.InfiniteTimeSpan)
            .AddHttpMessageHandler(provider =>
            {
                var photo = provider.GetRequiredService<IOptions<PhotoFixOptions>>().Value;
                return new AiUsageHttpMessageHandler(
                    provider.GetRequiredService<IAiUsageRecorder>(),
                    "OpenAI",
                    photo.ImageModel,
                    "agent-portrait-enhancement",
                    photo.AgentPortraitEstimatedCostUsd);
            });
        services.AddHttpClient<IPropertyStoryGenerator, OpenAIPropertyStoryGenerator>()
            .AddHttpMessageHandler(provider => OpenAiHandler(
                provider, "property-story", options => options.StoryEstimatedCostUsd));
        services.AddHttpClient<IVideoDirector, OpenAIVideoDirector>()
            .AddHttpMessageHandler(provider => OpenAiHandler(
                provider, "video-direction", options => options.VideoDirectionEstimatedCostUsd));
        services.AddHttpClient<ICampaignLocalizationService, OpenAICampaignLocalizationService>()
            .AddHttpMessageHandler(provider => OpenAiHandler(
                provider, "campaign-localization", options => options.LocalizationEstimatedCostUsd));
        services.AddHttpClient<ICampaignVideoChatAssistant, OpenAICampaignVideoChatAssistant>()
            .AddHttpMessageHandler(provider => OpenAiHandler(
                provider, "campaign-video-chat", options => options.VideoChatEstimatedCostUsd));
        services.AddHttpClient<IHelpChatAssistant, OpenAIHelpChatAssistant>()
            .AddHttpMessageHandler(provider => OpenAiHandler(
                provider, "help-chat", options => options.HelpChatEstimatedCostUsd));
        services.AddHttpClient<ElevenLabsVoiceProvider>()
            .AddHttpMessageHandler(provider =>
            {
                var voice = provider.GetRequiredService<IOptions<VoiceOptions>>().Value;
                return new AiUsageHttpMessageHandler(
                    provider.GetRequiredService<IAiUsageRecorder>(),
                    "ElevenLabs",
                    voice.ModelId,
                    "narration",
                    0,
                    estimateCost: (request, response, cancellationToken) =>
                        ElevenLabsUsageCostEstimator.EstimateAsync(
                            request,
                            response,
                            voice.EstimatedCostPerThousandCharactersUsd,
                            cancellationToken));
            });
        services.AddHttpClient<OpenAIVoiceProvider>()
            .AddHttpMessageHandler(provider =>
            {
                var voice = provider.GetRequiredService<IOptions<VoiceOptions>>().Value;
                return new AiUsageHttpMessageHandler(
                    provider.GetRequiredService<IAiUsageRecorder>(),
                    "OpenAI",
                    voice.OpenAIModel,
                    "narration",
                    provider.GetRequiredService<IOptions<OpenAIOptions>>().Value.SpeechEstimatedCostUsd);
            });
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

    private static AiUsageHttpMessageHandler OpenAiHandler(
        IServiceProvider provider,
        string operation,
        Func<OpenAIOptions, decimal> cost)
    {
        var options = provider.GetRequiredService<IOptions<OpenAIOptions>>().Value;
        return new AiUsageHttpMessageHandler(
            provider.GetRequiredService<IAiUsageRecorder>(),
            "OpenAI",
            string.IsNullOrWhiteSpace(options.Model) ? "unconfigured" : options.Model,
            operation,
            cost(options));
    }
}
