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
using ListingStudio.Application.Campaigns;
using ListingStudio.Domain.Campaigns;
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

    public FakePropertyVideoFrameExtractor PropertyVideoFrameExtractor { get; } = new();

    public AdjustableTimeProvider TimeProvider { get; } = new();

    public FakePropertyStoryGenerator StoryGenerator { get; } = new();

    public FakeVideoDirector VideoDirector { get; } = new();

    public FakeVoiceProvider VoiceProvider { get; } = new();

    public FakeCampaignLocalizationService CampaignLocalizationService { get; } = new();

    public FakeVideoRenderer VideoRenderer { get; } = new();

    public FakeAiVideoProvider AiVideoProvider { get; } = new();

    public FakeWalkthroughVideoProvider WalkthroughVideoProvider { get; } = new();

    public FakeBillingProviderGateway BillingProvider { get; } = new();

    public FakePhotoImageCodec PhotoImageCodec { get; } = new();

    public FakePropertyVideoClipFinder PropertyVideoClipFinder { get; } = new();

    public FakePhotoClutterRemover PhotoClutterRemover { get; } = new();

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
                ["Stripe:FreeTrialCampaignAllowance"] = "1",
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
            services.RemoveAll<IPropertyVideoFrameExtractor>();
            services.AddSingleton<IPropertyVideoFrameExtractor>(PropertyVideoFrameExtractor);
            services.RemoveAll<IPropertyVideoClipFinder>();
            services.AddSingleton<IPropertyVideoClipFinder>(PropertyVideoClipFinder);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(TimeProvider);
            services.RemoveAll<IPropertyStoryGenerator>();
            services.AddSingleton<IPropertyStoryGenerator>(StoryGenerator);
            services.RemoveAll<IVideoDirector>();
            services.AddSingleton<IVideoDirector>(VideoDirector);
            services.RemoveAll<IVoiceProvider>();
            services.AddSingleton<IVoiceProvider>(VoiceProvider);
            services.RemoveAll<ICampaignLocalizationService>();
            services.AddSingleton<ICampaignLocalizationService>(CampaignLocalizationService);
            services.RemoveAll<IVideoRenderer>();
            services.AddSingleton<IVideoRenderer>(VideoRenderer);
            services.RemoveAll<IAiVideoProvider>();
            services.AddSingleton<IAiVideoProvider>(AiVideoProvider);
            services.RemoveAll<IWalkthroughVideoProvider>();
            services.AddSingleton<IWalkthroughVideoProvider>(WalkthroughVideoProvider);
            services.RemoveAll<IBillingProviderGateway>();
            services.AddSingleton<IBillingProviderGateway>(BillingProvider);
            services.RemoveAll<IPhotoImageCodec>();
            services.AddSingleton<IPhotoImageCodec>(PhotoImageCodec);
            services.RemoveAll<IPhotoClutterRemover>();
            services.AddSingleton<IPhotoClutterRemover>(PhotoClutterRemover);
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

    private readonly ConcurrentQueue<byte[]> results = new();

    public void EnqueueFailure(Exception exception) => failures.Enqueue(exception);

    public void EnqueueResult(byte[] edited) => results.Enqueue(edited);

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

public sealed class FakePropertyVideoClipFinder : IPropertyVideoClipFinder
{
    /// <summary>The spans every video offers; none by default, so edits use photos.</summary>
    public IReadOnlyList<VideoClipSpan> Spans { get; set; } = [];

    public Task<IReadOnlyList<VideoClipSpan>> FindClipsAsync(
        Stream content,
        string originalFilename,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.True(content.CanRead);
        return Task.FromResult(Spans);
    }
}

public sealed class FakePropertyVideoFrameExtractor : IPropertyVideoFrameExtractor
{
    // An 8 by 6 pixel JPEG still.
    public static readonly byte[] Still = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAgAAAQABAAD//gAQTGF2YzYwLjMxLjEwMgD/2wBDAAgKCgsKCw0NDQ0NDRAPEBAQEBAQEBAQEBASEhIVFRUSEhIQEBISFBQVFRcXFxUVFRUXFxkZGR4eHBwjIyQrKzP/xABLAAEBAAAAAAAAAAAAAAAAAAAABQEBAAAAAAAAAAAAAAAAAAAABBABAAAAAAAAAAAAAAAAAAAAABEBAAAAAAAAAAAAAAAAAAAAAP/AABEIAAYACAMBIgACEQADEQD/2gAMAwEAAhEDEQA/AKwBYr//2Q==");

    private readonly ConcurrentQueue<object> outcomes = new();

    public int LastMaximumCandidates { get; private set; }

    /// <summary>Queues the stills the next extraction finds, one per timestamp.</summary>
    public void Enqueue(params int[] timestampsMs) => outcomes.Enqueue(timestampsMs);

    public void Enqueue(Exception exception) => outcomes.Enqueue(exception);

    public Task<IReadOnlyList<VideoFrameCandidate>> ExtractCandidatesAsync(
        Stream content,
        string originalFilename,
        int maximumCandidates,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.True(content.CanRead);
        LastMaximumCandidates = maximumCandidates;
        Assert.True(outcomes.TryDequeue(out var outcome), "A fake frame-extraction outcome must be queued.");
        return outcome switch
        {
            int[] timestamps => Task.FromResult<IReadOnlyList<VideoFrameCandidate>>(
                [.. timestamps.Select(timestamp => new VideoFrameCandidate(timestamp, 100, Still))]),
            Exception exception => Task.FromException<IReadOnlyList<VideoFrameCandidate>>(exception),
            _ => throw new InvalidOperationException("Unsupported fake frame-extraction outcome."),
        };
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
    private int walkthroughInputCount;

    public int CallCount => callCount;

    public int GeneratedClipInputCount => generatedClipInputCount;

    public int WalkthroughInputCount => walkthroughInputCount;

    private readonly ConcurrentQueue<byte[]> results = new();

    public void EnqueueFailure(Exception exception) => failures.Enqueue(exception);

    public void EnqueueResult(byte[] edited) => results.Enqueue(edited);

    public async Task<VideoRenderResult> RenderAsync(
        VideoRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref callCount);
        Interlocked.Add(ref generatedClipInputCount, request.GeneratedClips?.Count ?? 0);
        Interlocked.Add(ref walkthroughInputCount, request.WalkthroughTransitions?.Count ?? 0);
        Assert.All(request.WalkthroughTransitions ?? [], transition => Assert.True(File.Exists(transition.FilePath)));
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

public sealed class FakeWalkthroughVideoProvider : IWalkthroughVideoProvider
{
    private readonly ConcurrentQueue<Exception> submissionFailures = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<WalkthroughVideoState>> polls = new();
    private int submitCount;

    public bool IsEnabled => true;

    public string GenerationVersion => "fake-walkthrough-v1";

    public int ClipDurationMs => 5_000;

    public int SubmitCount => submitCount;

    /// <summary>The walking direction each transition was last submitted with, keyed by transition ID.</summary>
    public ConcurrentDictionary<Guid, WalkthroughDirection?> SubmittedDirections { get; } = new();

    public static byte[] ClipBytes { get; } = [0, 0, 0, 20, 102, 116, 121, 112, 119, 97, 108, 107];

    public void EnqueueSubmissionFailure(Exception exception) => submissionFailures.Enqueue(exception);

    /// <summary>Sets the next poll outcome for a transition; polls without one report the clip as still generating.</summary>
    public void EnqueuePoll(Guid transitionId, WalkthroughVideoState state) =>
        polls.GetOrAdd(transitionId.ToString("N"), _ => new()).Enqueue(state);

    public Task<string> SubmitAsync(
        WalkthroughVideoSubmission submission,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref submitCount);
        SubmittedDirections[Guid.ParseExact(submission.IdempotencyKey[..32], "N")] = submission.Direction;
        Assert.True(submission.StartFrame.Content.CanRead);
        Assert.True(submission.EndFrame.Content.CanRead);
        return submissionFailures.TryDequeue(out var failure)
            ? Task.FromException<string>(failure)
            : Task.FromResult($"fake-{submission.IdempotencyKey}");
    }

    public Task<WalkthroughVideoPollResult> PollAsync(
        string providerRequestId,
        CancellationToken cancellationToken = default)
    {
        var transitionKey = providerRequestId["fake-".Length..][..32];
        var state = polls.TryGetValue(transitionKey, out var queue) && queue.TryDequeue(out var next)
            ? next
            : WalkthroughVideoState.InProgress;
        return Task.FromResult(new WalkthroughVideoPollResult(
            state,
            state == WalkthroughVideoState.Completed ? new MemoryStream(ClipBytes) : null,
            "fake-higgsfield",
            "fake-model",
            state == WalkthroughVideoState.Failed ? "The fake provider rejected the photos." : null));
    }
}

public sealed class FakeAiVideoProvider : IAiVideoProvider
{
    private readonly ConcurrentQueue<Exception> failures = new();
    private int callCount;

    public bool IsEnabled => true;

    public string GenerationVersion => "fake-ai-video-v1";

    public int CallCount => callCount;

    private readonly ConcurrentQueue<byte[]> results = new();

    public void EnqueueFailure(Exception exception) => failures.Enqueue(exception);

    public void EnqueueResult(byte[] edited) => results.Enqueue(edited);

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

public sealed class FakeCampaignLocalizationService : ICampaignLocalizationService
{
    private readonly ConcurrentQueue<Func<CampaignLocalizationRequest, CampaignLocalizedCopy>> outcomes = new();
    private int callCount;

    public int CallCount => callCount;

    public void Enqueue(Func<CampaignLocalizationRequest, CampaignLocalizedCopy> factory) => outcomes.Enqueue(factory);

    public Task<CampaignLocalizedCopy> LocalizeAsync(
        CampaignLocalizationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref callCount);
        if (outcomes.TryDequeue(out var outcome))
        {
            return Task.FromResult(outcome(request));
        }

        var original = request.NarrationSegments
            .Select(segment => new CampaignLocalizedSegment(segment.Id, segment.Text))
            .ToArray();
        return Task.FromResult(new CampaignLocalizedCopy(original, original));
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

/// <summary>
/// Stands in for FFmpeg: every photo decodes as a dim 400 by 300 landscape, scaled the way the request asks, and
/// encodes to a fixed small JPEG or PNG.
/// </summary>
public sealed class FakePhotoImageCodec : IPhotoImageCodec
{
    public const int SourceWidth = 400;
    public const int SourceHeight = 300;
    public const byte SourceLevel = 60;
    public const byte EditedLevel = 200;

    public static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Z+VAAAAAASUVORK5CYII=");

    // What the fake clutter remover returns; it decodes like the source photo with one small item removed.
    public static readonly byte[] EditedPng = [.. Png, 0];

    // A clutter remover result that redrew the room: it decodes to a different scene altogether.
    public static readonly byte[] RedrawnPng = [.. Png, 1];

    // A structurally detailed test photo tilted clockwise by about five degrees.
    public static readonly byte[] TiltedPng = [.. Png, 2];

    public static byte[] Jpeg => FakePropertyVideoFrameExtractor.Still;

    public Task<PhotoPixels> DecodeAsync(
        byte[] image,
        string mimeType,
        int boxWidth,
        int boxHeight,
        PhotoScaling scaling,
        CancellationToken cancellationToken = default)
    {
        var (width, height) = scaling switch
        {
            PhotoScaling.Exact => (boxWidth, boxHeight),
            PhotoScaling.Fit => FitInside(boxWidth, boxHeight),
            _ => boxWidth >= SourceWidth && boxHeight >= SourceHeight ? (SourceWidth, SourceHeight) : FitInside(boxWidth, boxHeight),
        };
        var pixels = new byte[width * height * 3];
        pixels.AsSpan().Fill(SourceLevel);
        if (image.AsSpan().SequenceEqual(TiltedPng))
        {
            FillTiltedGrid(pixels, width, height, 5);
        }
        else if (image.AsSpan().SequenceEqual(RedrawnPng))
        {
            for (var pixel = 0; pixel < width * height; pixel++)
            {
                pixels.AsSpan(pixel * 3, 3).Fill((pixel % width / 20 + pixel / width / 20) % 2 == 0 ? (byte)230 : (byte)30);
            }
        }
        else if (image.AsSpan().SequenceEqual(EditedPng))
        {
            // An edited photo from the clutter remover: one small item, a tenth of each side, is gone.
            for (var y = height * 2 / 5; y < height / 2; y++)
            {
                pixels.AsSpan((y * width + width * 2 / 5) * 3, width / 10 * 3).Fill(EditedLevel);
            }
        }

        return Task.FromResult(new PhotoPixels(width, height, pixels));
    }

    public Task<byte[]> EncodeJpegAsync(PhotoPixels photo, CancellationToken cancellationToken = default) =>
        Task.FromResult(Jpeg.ToArray());

    public Task<byte[]> EncodePngAsync(PhotoPixels photo, CancellationToken cancellationToken = default) =>
        Task.FromResult(Png.ToArray());

    public Task<byte[]> EncodeMaskPngAsync(
        int width,
        int height,
        PhotoRegion editable,
        CancellationToken cancellationToken = default) => Task.FromResult(Png.ToArray());

    private static (int Width, int Height) FitInside(int boxWidth, int boxHeight)
    {
        var scale = Math.Min(boxWidth / (double)SourceWidth, boxHeight / (double)SourceHeight);
        return (Math.Max(1, (int)Math.Round(SourceWidth * scale)), Math.Max(1, (int)Math.Round(SourceHeight * scale)));
    }

    private static void FillTiltedGrid(byte[] pixels, int width, int height, double angleDegrees)
    {
        pixels.AsSpan().Fill(232);
        var radians = angleDegrees * Math.PI / 180;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        var centerX = (width - 1) / 2.0;
        var centerY = (height - 1) / 2.0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offsetX = x - centerX;
                var offsetY = y - centerY;
                var gridX = offsetX * cosine + offsetY * sine;
                var gridY = -offsetX * sine + offsetY * cosine;
                var distanceX = Math.Abs(gridX - Math.Round(gridX / 72) * 72);
                var distanceY = Math.Abs(gridY - Math.Round(gridY / 54) * 54);
                if (distanceX > 1.25 && distanceY > 1.25)
                {
                    continue;
                }

                pixels.AsSpan((y * width + x) * 3, 3).Fill(28);
            }
        }
    }
}

public sealed class FakePhotoClutterRemover : IPhotoClutterRemover
{
    private readonly ConcurrentQueue<Exception> failures = new();

    public bool IsEnabled { get; set; } = true;

    public ConcurrentQueue<PhotoClutterRemovalRequest> Requests { get; } = new();

    private readonly ConcurrentQueue<byte[]> results = new();

    public void EnqueueFailure(Exception exception) => failures.Enqueue(exception);

    public void EnqueueResult(byte[] edited) => results.Enqueue(edited);

    public Task<byte[]> RemoveClutterAsync(
        PhotoClutterRemovalRequest request,
        CancellationToken cancellationToken = default)
    {
        Requests.Enqueue(request);
        return failures.TryDequeue(out var failure)
            ? Task.FromException<byte[]>(failure)
            : Task.FromResult(results.TryDequeue(out var result) ? result : FakePhotoImageCodec.EditedPng.ToArray());
    }
}
