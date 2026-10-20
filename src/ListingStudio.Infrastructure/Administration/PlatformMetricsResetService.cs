using System.Security.Cryptography;
using System.Text;
using ListingStudio.Application.Administration;
using ListingStudio.Domain.Administration;
using ListingStudio.Infrastructure.Persistence;

namespace ListingStudio.Infrastructure.Administration;

public sealed class PlatformMetricsResetService(
    ApplicationDbContext dbContext,
    TimeProvider timeProvider) : IPlatformAdminMetricsResetService
{
    public async Task<DateTimeOffset> ResetAsync(
        string actorUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        var resetAtUtc = timeProvider.GetUtcNow();

        dbContext.PlatformMetricsResets.Add(PlatformMetricsReset.Create(resetAtUtc));
        dbContext.PlatformAdminAuditEvents.Add(PlatformAdminAuditEvent.Create(
            "platform-metrics.reset",
            "platform-admin",
            Pseudonym(actorUserId),
            "succeeded",
            resetAtUtc));
        await dbContext.SaveChangesAsync(cancellationToken);
        return resetAtUtc;
    }

    private static string Pseudonym(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
