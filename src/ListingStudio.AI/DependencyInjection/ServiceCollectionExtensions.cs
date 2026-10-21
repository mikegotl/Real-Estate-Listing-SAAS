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
        services.AddHttpClient<IPropertyStoryGenerator, OpenAIPropertyStoryGenerator>()
            .AddHttpMessageHandler(provider => OpenAiHandler(
                provider, "property-story", options => options.StoryEstimatedCostUsd));
        services.AddHttpClient<IVideoDirector, OpenAIVideoDirector>()
            .AddHttpMessageHandler(provider => OpenAiHandler(
                provider, "video-direction", options => options.VideoDirectionEstimatedCostUsd));
        services.AddHttpClient<ICampaignLocalizationService, OpenAICampaignLocalizationService>()
            .AddHttpMessageHandler(provider => OpenAiHandler(
                provider, "campaign-localization", options => options.LocalizationEstimatedCostUsd));
        services.AddHttpClient<ElevenLabsVoiceProvider>()
            .AddHttpMessageHandler(provider =>
            {
                var voice = provider.GetRequiredService<IOptions<VoiceOptions>>().Value;
                return new AiUsageHttpMessageHandler(
                    provider.GetRequiredService<IAiUsageRecorder>(),
                    "ElevenLabs",
                    voice.ModelId,
                    "narration",
                    voice.EstimatedCostUsd);
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
