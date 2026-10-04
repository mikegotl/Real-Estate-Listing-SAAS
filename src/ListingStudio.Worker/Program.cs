using ListingStudio.AI.Configuration;
using ListingStudio.AI.DependencyInjection;
using ListingStudio.Application.DependencyInjection;
using ListingStudio.Infrastructure.DependencyInjection;
using ListingStudio.Video.DependencyInjection;
using ListingStudio.Worker;

var builder = Host.CreateApplicationBuilder(args);
var mediaAnalysisEnabled = builder.Configuration.GetValue<bool>($"{MediaAnalysisWorkerOptions.SectionName}:Enabled");
builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration)
    .AddAI(builder.Configuration)
    .AddVideo(builder.Configuration);
builder.Services
    .AddOptions<MediaAnalysisWorkerOptions>()
    .Bind(builder.Configuration.GetSection(MediaAnalysisWorkerOptions.SectionName))
    .Validate(
        options => options.PollIntervalSeconds is >= 1 and <= 300,
        "MediaAnalysis:PollIntervalSeconds must be between 1 and 300.")
    .ValidateOnStart();
builder.Services
    .AddOptions<OpenAIOptions>()
    .Validate(
        options => !mediaAnalysisEnabled
            || (!string.IsNullOrWhiteSpace(options.ApiKey)
                && !string.IsNullOrWhiteSpace(options.Model)
                && Uri.TryCreate(options.ResponsesEndpoint, UriKind.Absolute, out var endpoint)
                && endpoint.Scheme == Uri.UriSchemeHttps),
        "Enabled media analysis requires OpenAI:ApiKey, OpenAI:Model, and an HTTPS OpenAI:ResponsesEndpoint.")
    .ValidateOnStart();
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<CampaignGenerationWorker>();

await builder.Build().RunAsync();
