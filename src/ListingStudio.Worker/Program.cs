using ListingStudio.AI.Configuration;
using ListingStudio.AI.DependencyInjection;
using ListingStudio.Application.DependencyInjection;
using ListingStudio.Infrastructure.DependencyInjection;
using ListingStudio.Video.DependencyInjection;
using ListingStudio.Worker;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Builder;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddListingStudioDataProtection(builder.Configuration, builder.Environment);

if (builder.Environment.IsProduction())
{
    builder.Logging.ClearProviders();
    builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
}

if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
    var managedIdentityClientId = builder.Configuration["AZURE_CLIENT_ID"];
    var credential = string.IsNullOrWhiteSpace(managedIdentityClientId)
        ? new DefaultAzureCredential()
        : new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ManagedIdentityClientId = managedIdentityClientId,
        });
    builder.Services.AddOpenTelemetry().UseAzureMonitor(options => options.Credential = credential);
}

var mediaAnalysisEnabled = builder.Configuration.GetValue<bool>($"{MediaAnalysisWorkerOptions.SectionName}:Enabled");
var clutterRemovalEnabled = builder.Configuration.GetValue<bool>($"{PhotoFixOptions.SectionName}:RemoveClutterEnabled");
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
    .AddOptions<VideoProcessingWorkerOptions>()
    .Bind(builder.Configuration.GetSection(VideoProcessingWorkerOptions.SectionName))
    .Validate(
        options => options.PollIntervalSeconds is >= 1 and <= 300,
        "VideoProcessing:PollIntervalSeconds must be between 1 and 300.")
    .ValidateOnStart();
builder.Services
    .AddOptions<PhotoFixWorkerOptions>()
    .Bind(builder.Configuration.GetSection(PhotoFixWorkerOptions.SectionName))
    .Validate(
        options => options.PollIntervalSeconds is >= 1 and <= 300,
        "PhotoFixes:PollIntervalSeconds must be between 1 and 300.")
    .ValidateOnStart();
builder.Services
    .AddOptions<CommentEmailWorkerOptions>()
    .Bind(builder.Configuration.GetSection(CommentEmailWorkerOptions.SectionName))
    .Validate(
        options => options.PollIntervalSeconds is >= 1 and <= 300,
        "Email:Comments:PollIntervalSeconds must be between 1 and 300.")
    .ValidateOnStart();
builder.Services
    .AddOptions<OpenAIOptions>()
    .Validate(
        options => !clutterRemovalEnabled || !string.IsNullOrWhiteSpace(options.ApiKey),
        "PhotoFixes:RemoveClutterEnabled requires OpenAI:ApiKey.")
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
builder.Services.AddHostedService<PropertyVideoProcessingWorker>();
builder.Services.AddHostedService<PropertyVideoFrameExtractionWorker>();
builder.Services.AddHostedService<CampaignGenerationWorker>();
builder.Services.AddHostedService<TikTokTestSuiteWorker>();
builder.Services.AddHostedService<WalkthroughTransitionWorker>();
builder.Services.AddHostedService<AerialIntroWorker>();
builder.Services.AddHostedService<PhotoFixWorker>();
builder.Services.AddHostedService<CommentEmailWorker>();
builder.Services.AddHostedService<AdminUserEmailWorker>();
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"]);

var app = builder.Build();
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("live"),
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready"),
});
await app.RunAsync();

