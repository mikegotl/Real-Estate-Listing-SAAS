using System.Collections.Concurrent;
using ListingStudio.Application.Properties;
using ListingStudio.Application.Stories;
using ListingStudio.Application.Videos;
using ListingStudio.Application.Audio;
using ListingStudio.Domain.Properties;
using ListingStudio.Domain.Stories;
using ListingStudio.Domain.Videos;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;
using Xunit;
using ListingStudio.Application.Billing;
using ListingStudio.Infrastructure.Billing;
using ListingStudio.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

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

    public ListingStudioWebApplicationFactory CreateFactory(
        Action<IServiceCollection> configureServices) =>
        new(postgreSql.GetConnectionString(), mediaRootPath, configureServices);

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

public sealed class ListingStudioWebApplicationFactory(
    string connectionString,
    string mediaRootPath,
    Action<IServiceCollection>? additionalServices = null)
    : WebApplicationFactory<Program>
{
    public FakePropertyMediaAnalyzer MediaAnalyzer { get; } = new();

    public FakePropertyVideoTranscoder PropertyVideoTranscoder { get; } = new();

    public AdjustableTimeProvider TimeProvider { get; } = new();

    public FakePropertyStoryGenerator StoryGenerator { get; } = new();

    public FakeVideoDirector VideoDirector { get; } = new();

    public FakeVoiceProvider VoiceProvider { get; } = new();

    public FakeVideoRenderer VideoRenderer { get; } = new();

    public FakeAiVideoProvider AiVideoProvider { get; } = new();

    public FakeBillingProviderGateway BillingProvider { get; } = new();

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
                ["FFmpeg:ExecutablePath"] = "/usr/bin/true",
                ["Stripe:WebhookSecret"] = FakeBillingProviderGateway.WebhookSecret,
                ["Stripe:StarterPriceId"] = FakeBillingProviderGateway.StarterPriceId,
                ["Stripe:ProfessionalPriceId"] = FakeBillingProviderGateway.ProfessionalPriceId,
                ["Stripe:StarterMonthlyCampaignAllowance"] = "2",
                ["Stripe:ProfessionalMonthlyCampaignAllowance"] = "5",
            });
        });
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPropertyMediaAnalyzer>();
            services.AddSingleton<IPropertyMediaAnalyzer>(MediaAnalyzer);
            services.RemoveAll<IPropertyVideoTranscoder>();
            services.AddSingleton<IPropertyVideoTranscoder>(PropertyVideoTranscoder);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(TimeProvider);
            services.RemoveAll<IPropertyStoryGenerator>();
            services.AddSingleton<IPropertyStoryGenerator>(StoryGenerator);
            services.RemoveAll<IVideoDirector>();
            services.AddSingleton<IVideoDirector>(VideoDirector);
            services.RemoveAll<IVoiceProvider>();
            services.AddSingleton<IVoiceProvider>(VoiceProvider);
            services.RemoveAll<IVideoRenderer>();
            services.AddSingleton<IVideoRenderer>(VideoRenderer);
            services.RemoveAll<IAiVideoProvider>();
            services.AddSingleton<IAiVideoProvider>(AiVideoProvider);
            services.RemoveAll<IBillingProviderGateway>();
            services.AddSingleton<IBillingProviderGateway>(BillingProvider);
            additionalServices?.Invoke(services);
        });
    }
}

public sealed class FakePropertyVideoTranscoder : IPropertyVideoTranscoder
{
    private readonly ConcurrentQueue<Exception> failures = new();
    private int probeCallCount;
    private int enhancementCallCount;

    public string EnhancementVersion => "fake-stabilize-color-v1";
    public int ProbeCallCount => probeCallCount;
    public int EnhancementCallCount => enhancementCallCount;
    public PropertyVideoMetadata Metadata { get; set; } = new("mov,mp4,m4a,3gp,3g2,mj2", 1_920, 1_080, 12_000, 29.97m, true);

    public void EnqueueFailure(Exception exception) => failures.Enqueue(exception);

    public Task<PropertyVideoMetadata> ProbeAsync(
        Stream content,
        string originalFilename,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref probeCallCount);
        Assert.True(content.CanRead);
        return Task.FromResult(Metadata);
    }

    public Task<PropertyVideoEnhancementResult> EnhanceAsync(
        Stream content,
        string originalFilename,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref enhancementCallCount);
        Assert.True(content.CanRead);
        if (failures.TryDequeue(out var failure))
        {
            return Task.FromException<PropertyVideoEnhancementResult>(failure);
        }

        byte[] enhanced = [0, 0, 0, 20, 102, 116, 121, 112, 105, 115, 111, 109, 1, 2, 3, 4];
        return Task.FromResult(new PropertyVideoEnhancementResult(
            new MemoryStream(enhanced),
            enhanced.Length,
            Metadata with { FrameRate = 30m }));
    }
}

public sealed class FakeBillingProviderGateway : IBillingProviderGateway
{
    public const string WebhookSecret = "whsec_integration_test_only";
    public const string StarterPriceId = "price_starter_test";
    public const string ProfessionalPriceId = "price_professional_test";

    private int customerCallCount;
    private int checkoutCallCount;
    private int portalCallCount;

    public int CustomerCallCount => customerCallCount;

    public int CheckoutCallCount => checkoutCallCount;

    public int PortalCallCount => portalCallCount;

    public BillingProviderCheckoutRequest? LastCheckoutRequest { get; private set; }

    public Task<string> CreateCustomerAsync(
        BillingProviderCustomerRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref customerCallCount);
        return Task.FromResult($"cus_test_{request.OrganizationId:N}");
    }

    public Task<BillingRedirect> CreateCheckoutSessionAsync(
        BillingProviderCheckoutRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref checkoutCallCount);
        LastCheckoutRequest = request;
        return Task.FromResult(new BillingRedirect("https://checkout.stripe.test/session"));
    }

    public Task<BillingRedirect> CreatePortalSessionAsync(
        BillingProviderPortalRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref portalCallCount);
        return Task.FromResult(new BillingRedirect("https://billing.stripe.test/session"));
    }

    public object ParseAndVerifyWebhook(string payload, string signatureHeader)
    {
        var gateway = new StripeBillingGateway(
            new HttpClient { BaseAddress = new Uri("https://api.stripe.test") },
            Options.Create(new StripeOptions
            {
                WebhookSecret = WebhookSecret,
                StarterPriceId = StarterPriceId,
                ProfessionalPriceId = ProfessionalPriceId,
                WebhookToleranceSeconds = 300,
            }),
            TimeProvider.System);
        return gateway.ParseAndVerifyWebhook(payload, signatureHeader);
    }
}

public sealed class FakeVideoRenderer : IVideoRenderer
{
    private readonly ConcurrentQueue<Exception> failures = new();
    private int callCount;
    private int generatedClipInputCount;

    public int CallCount => callCount;

    public int GeneratedClipInputCount => generatedClipInputCount;

    public void EnqueueFailure(Exception exception) => failures.Enqueue(exception);

    public async Task<VideoRenderResult> RenderAsync(
        VideoRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref callCount);
        Interlocked.Add(ref generatedClipInputCount, request.GeneratedClips?.Count ?? 0);
        if (failures.TryDequeue(out var failure))
        {
            throw failure;
        }

        await File.WriteAllBytesAsync(request.OutputFilePath, [0, 1, 2, 3, 4, 5], cancellationToken);
        return new VideoRenderResult(
            request.OutputFilePath,
            0,
            TimeSpan.FromMilliseconds(10),
            string.Empty,
            string.Empty);
    }
}

public sealed class FakeAiVideoProvider : IAiVideoProvider
{
    private readonly ConcurrentQueue<Exception> failures = new();
    private int callCount;

    public bool IsEnabled => true;

    public string GenerationVersion => "fake-ai-video-v1";

    public int CallCount => callCount;

    public void EnqueueFailure(Exception exception) => failures.Enqueue(exception);

    public Task<AiVideoProviderResult> GenerateAsync(
        AiVideoProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref callCount);
        if (failures.TryDequeue(out var failure))
        {
            return Task.FromException<AiVideoProviderResult>(failure);
        }

        Assert.True(request.Content.CanRead);
        var landscape = request.AspectRatio == VideoAspectRatio.Landscape16By9;
        return Task.FromResult(new AiVideoProviderResult(
            new MemoryStream([0, 0, 0, 20, 102, 116, 121, 112, 105, 115, 111, 109]),
            "video/mp4",
            "fake-provider",
            "fake-model",
            $"fake-request-{callCount}",
            request.DurationMs,
            landscape ? 1_920 : 1_080,
            landscape ? 1_080 : 1_920,
            0.125m,
            new Dictionary<string, string> { ["mode"] = "test" }));
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
    private readonly ConcurrentQueue<Func<VideoDirectionRequest, IReadOnlyList<string>, DirectedEditorialPlan>> outcomes = new();
    private int callCount;

    public string DirectorVersion => "fake-video-director-v1";

    public int CallCount => callCount;

    public VideoDirectionRequest? LastRequest { get; private set; }

    public IReadOnlyList<string> LastValidationFeedback { get; private set; } = [];

    public void Enqueue(Func<VideoDirectionRequest, DirectedEditorialPlan> factory) =>
        outcomes.Enqueue((request, _) => factory(request));

    public void EnqueueRepair(
        Func<VideoDirectionRequest, IReadOnlyList<string>, DirectedEditorialPlan> factory) =>
        outcomes.Enqueue(factory);

    public Task<DirectedEditorialPlan> DirectAsync(
        VideoDirectionRequest request,
        IReadOnlyList<string>? validationFeedback = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref callCount);
        LastRequest = request;
        LastValidationFeedback = validationFeedback ?? [];
        Assert.True(outcomes.TryDequeue(out var outcome), "A fake video-direction outcome must be queued.");
        return Task.FromResult(outcome(request, LastValidationFeedback));
    }
}

public sealed class FakePropertyStoryGenerator : IPropertyStoryGenerator
{
    private readonly ConcurrentQueue<object> outcomes = new();
    private int callCount;

    public string GenerationVersion => "fake-story-v1";

    public int CallCount => callCount;

    public PropertyStoryGenerationRequest? LastRequest { get; private set; }

    public void Enqueue(PropertyStoryContent content) => outcomes.Enqueue(content);

    public void Enqueue(Exception exception) => outcomes.Enqueue(exception);

    public Task<PropertyStoryContent> GenerateAsync(
        PropertyStoryGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref callCount);
        LastRequest = request;
        Assert.True(outcomes.TryDequeue(out var outcome), "A fake story-generation outcome must be queued.");
        return outcome switch
        {
            PropertyStoryContent story => Task.FromResult(story),
            Exception exception => Task.FromException<PropertyStoryContent>(exception),
            _ => throw new InvalidOperationException("Unsupported fake story-generation outcome."),
        };
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
