using System.Collections.Concurrent;
using ListingStudio.Application.Properties;
using ListingStudio.Application.Stories;
using ListingStudio.Application.Videos;
using ListingStudio.Application.Audio;
using ListingStudio.Domain.Properties;
using ListingStudio.Domain.Stories;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;
using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed class PostgreSqlWebApplicationFixture : IAsyncLifetime
{
    private readonly string mediaRootPath = Path.Combine(
        Path.GetTempPath(),
        $"listingstudio-integration-media-{Guid.NewGuid():N}");
    private readonly PostgreSqlContainer postgreSql = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("listingstudio_tests")
        .WithUsername("listingstudio")
        .WithPassword("integration-test-password")
        .Build();

    public ListingStudioWebApplicationFactory Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await postgreSql.StartAsync();
        Factory = new ListingStudioWebApplicationFactory(postgreSql.GetConnectionString(), mediaRootPath);

        await using var scope = Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await postgreSql.DisposeAsync();
        if (Directory.Exists(mediaRootPath))
        {
            Directory.Delete(mediaRootPath, recursive: true);
        }
    }
}

public sealed class ListingStudioWebApplicationFactory(string connectionString, string mediaRootPath)
    : WebApplicationFactory<Program>
{
    public FakePropertyMediaAnalyzer MediaAnalyzer { get; } = new();

    public AdjustableTimeProvider TimeProvider { get; } = new();

    public FakePropertyStoryGenerator StoryGenerator { get; } = new();

    public FakeVideoDirector VideoDirector { get; } = new();

    public FakeVoiceProvider VoiceProvider { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PostgreSQL:ConnectionString"] = connectionString,
                ["AzureBlobStorage:Provider"] = "Local",
                ["AzureBlobStorage:LocalRootPath"] = mediaRootPath,
                ["AzureBlobStorage:CampaignLocalRootPath"] = Path.Combine(mediaRootPath, "campaign-assets"),
            });
        });
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPropertyMediaAnalyzer>();
            services.AddSingleton<IPropertyMediaAnalyzer>(MediaAnalyzer);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(TimeProvider);
            services.RemoveAll<IPropertyStoryGenerator>();
            services.AddSingleton<IPropertyStoryGenerator>(StoryGenerator);
            services.RemoveAll<IVideoDirector>();
            services.AddSingleton<IVideoDirector>(VideoDirector);
            services.RemoveAll<IVoiceProvider>();
            services.AddSingleton<IVoiceProvider>(VoiceProvider);
        });
    }
}

public sealed class FakeVoiceProvider : IVoiceProvider
{
    private readonly ConcurrentQueue<Func<VoiceGenerationRequest, VoiceGenerationResult>> outcomes = new();
    private int callCount;

    public string GenerationVersion { get; private set; } = "fake-voice-v1";

    public int CallCount => callCount;

    public VoiceGenerationRequest? LastRequest { get; private set; }

    public void SetGenerationVersion(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        GenerationVersion = value;
    }

    public void Enqueue(Func<VoiceGenerationRequest, VoiceGenerationResult> factory) => outcomes.Enqueue(factory);

    public Task<VoiceGenerationResult> GenerateAsync(
        VoiceGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref callCount);
        LastRequest = request;
        Assert.True(outcomes.TryDequeue(out var outcome), "A fake voice-generation outcome must be queued.");
        return Task.FromResult(outcome(request));
    }
}

public sealed class FakeVideoDirector : IVideoDirector
{
    private readonly ConcurrentQueue<Func<VideoDirectionRequest, DirectedEditorialPlan>> outcomes = new();
    private int callCount;

    public string DirectorVersion => "fake-video-director-v1";

    public int CallCount => callCount;

    public VideoDirectionRequest? LastRequest { get; private set; }

    public void Enqueue(Func<VideoDirectionRequest, DirectedEditorialPlan> factory) => outcomes.Enqueue(factory);

    public Task<DirectedEditorialPlan> DirectAsync(
        VideoDirectionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref callCount);
        LastRequest = request;
        Assert.True(outcomes.TryDequeue(out var outcome), "A fake video-direction outcome must be queued.");
        return Task.FromResult(outcome(request));
    }
}

public sealed class FakePropertyStoryGenerator : IPropertyStoryGenerator
{
    private readonly ConcurrentQueue<PropertyStoryContent> outcomes = new();
    private int callCount;

    public string GenerationVersion => "fake-story-v1";

    public int CallCount => callCount;

    public PropertyStoryGenerationRequest? LastRequest { get; private set; }

    public void Enqueue(PropertyStoryContent content) => outcomes.Enqueue(content);

    public Task<PropertyStoryContent> GenerateAsync(
        PropertyStoryGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref callCount);
        LastRequest = request;
        Assert.True(outcomes.TryDequeue(out var outcome), "A fake story-generation outcome must be queued.");
        return Task.FromResult(outcome);
    }
}

public sealed class AdjustableTimeProvider : TimeProvider
{
    private DateTimeOffset utcNow = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => utcNow;

    public void Advance(TimeSpan duration) => utcNow = utcNow.Add(duration);
}

public sealed class FakePropertyMediaAnalyzer : IPropertyMediaAnalyzer
{
    private readonly ConcurrentQueue<object> outcomes = new();

    public void Enqueue(PropertyMediaAnalysis analysis) => outcomes.Enqueue(analysis);

    public void Enqueue(Exception exception) => outcomes.Enqueue(exception);

    public Task<PropertyMediaAnalysis> AnalyzeAsync(
        PropertyMediaAnalysisInput input,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.True(input.Content.CanRead);
        Assert.True(outcomes.TryDequeue(out var outcome), "A fake media-analysis outcome must be queued.");
        return outcome switch
        {
            PropertyMediaAnalysis analysis => Task.FromResult(analysis),
            Exception exception => Task.FromException<PropertyMediaAnalysis>(exception),
            _ => throw new InvalidOperationException("Unsupported fake media-analysis outcome."),
        };
    }
}
