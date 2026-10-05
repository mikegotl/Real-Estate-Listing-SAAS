using ListingStudio.Application.Campaigns;
using ListingStudio.Application.Properties;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Infrastructure.Configuration;
using ListingStudio.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class BackgroundWorkerResilienceTests
{
    [Fact]
    public async Task CampaignWorkerContinuesAfterInfrastructureFailure()
    {
        var state = new CampaignProcessorState();
        await using var services = new ServiceCollection()
            .AddSingleton(state)
            .AddScoped<ICampaignGenerationProcessor, RecoveringCampaignProcessor>()
            .BuildServiceProvider();
        var worker = new CampaignGenerationWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new CampaignGenerationOptions { PollIntervalSeconds = 1 }),
            NullLogger<CampaignGenerationWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await state.Recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);

        Assert.True(state.Attempts >= 2);
    }

    [Fact]
    public async Task MediaAnalysisWorkerContinuesAfterInfrastructureFailure()
    {
        var state = new MediaProcessorState();
        await using var services = new ServiceCollection()
            .AddSingleton(state)
            .AddScoped<IPropertyMediaAnalysisProcessor, RecoveringMediaProcessor>()
            .BuildServiceProvider();
        var worker = new ListingStudio.Worker.Worker(
            services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new MediaAnalysisWorkerOptions { Enabled = true, PollIntervalSeconds = 1 }),
            NullLogger<ListingStudio.Worker.Worker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await state.Recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);

        Assert.True(state.Attempts >= 2);
    }

    private sealed class CampaignProcessorState
    {
        public int Attempts;
        public TaskCompletionSource Recovered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class RecoveringCampaignProcessor(CampaignProcessorState state) : ICampaignGenerationProcessor
    {
        public Task<CampaignStageRunResult?> ProcessNextStageAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref state.Attempts) == 1)
            {
                throw new InvalidOperationException("Simulated database outage.");
            }

            state.Recovered.TrySetResult();
            return Task.FromResult<CampaignStageRunResult?>(new(
                Guid.NewGuid(),
                CampaignGenerationStage.ValidateProperty,
                true,
                true,
                false,
                1,
                null));
        }
    }

    private sealed class MediaProcessorState
    {
        public int Attempts;
        public TaskCompletionSource Recovered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class RecoveringMediaProcessor(MediaProcessorState state) : IPropertyMediaAnalysisProcessor
    {
        public Task<PropertyMediaAnalysisRunResult?> AnalyzeNextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref state.Attempts) == 1)
            {
                throw new InvalidOperationException("Simulated database outage.");
            }

            state.Recovered.TrySetResult();
            return Task.FromResult<PropertyMediaAnalysisRunResult?>(null);
        }

        public Task<PropertyMediaAnalysisRunResult?> AnalyzeNextForPropertyAsync(
            Guid organizationId,
            Guid propertyId,
            CancellationToken cancellationToken = default) =>
            AnalyzeNextAsync(cancellationToken);
    }
}
