using ListingStudio.Application.Administration;
using ListingStudio.Domain.Administration;
using ListingStudio.Infrastructure.Identity;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ListingStudio.Infrastructure.Administration;

public sealed partial class OperationalEventRecorder(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<OperationalEventRecorder> logger) : IOperationalEventRecorder, IAiUsageRecorder
{
    public async ValueTask RecordAsync(
        OperationalObservation observation,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            dbContext.OperationalEvents.Add(OperationalEvent.Create(
                observation.Kind,
                observation.Category,
                observation.Outcome,
                observation.OccurredAtUtc));
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogOperationalEventFailure(logger, exception);
        }
    }

    public async ValueTask RecordSuccessfulSignInAsync(
        string userId,
        string method,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await dbContext.Users.SingleAsync(item => item.Id == userId, cancellationToken);
            user.RecordSuccessfulSignIn(occurredAtUtc);
            dbContext.OperationalEvents.Add(OperationalEvent.Create("sign-in", method, "succeeded", occurredAtUtc));
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogSignInFailure(logger, exception);
        }
    }

    async ValueTask IAiUsageRecorder.RecordAsync(
        AiCallObservation observation,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            dbContext.AiUsageEvents.Add(AiUsageEvent.Create(
                observation.Provider,
                observation.Model,
                observation.Operation,
                observation.Succeeded,
                (long)Math.Ceiling(observation.Duration.TotalMilliseconds),
                observation.EstimatedCostUsd,
                observation.FailureCategory,
                timeProvider.GetUtcNow()));
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogAiUsageFailure(logger, exception);
        }
    }

    [LoggerMessage(1, LogLevel.Warning, "Unable to persist a privacy-safe operational event.")]
    private static partial void LogOperationalEventFailure(ILogger logger, Exception exception);

    [LoggerMessage(2, LogLevel.Warning, "Unable to persist a successful sign-in observation.")]
    private static partial void LogSignInFailure(ILogger logger, Exception exception);

    [LoggerMessage(3, LogLevel.Warning, "Unable to persist a privacy-safe AI usage event.")]
    private static partial void LogAiUsageFailure(ILogger logger, Exception exception);
}
