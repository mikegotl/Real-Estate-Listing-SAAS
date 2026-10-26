using ListingStudio.Application.Administration;
using ListingStudio.Domain.Administration;
using ListingStudio.Infrastructure.Identity;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;

namespace ListingStudio.Infrastructure.Administration;

public sealed class PlatformAdminImpersonationAudit(
    UserManager<ApplicationUser> userManager,
    ApplicationDbContext dbContext,
    TimeProvider timeProvider) : IPlatformAdminImpersonationAudit
{
    public async Task<ImpersonationStartResult> AuthorizeStartAsync(
        string administratorUserId,
        string targetUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(administratorUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetUserId);

        var administrator = await userManager.FindByIdAsync(administratorUserId);
        var target = await userManager.FindByIdAsync(targetUserId);
        var failure = administrator is null || !await userManager.IsInRoleAsync(administrator, PlatformAdministration.Role)
            ? ImpersonationStartFailure.NotPlatformAdmin
            : target is null
                ? ImpersonationStartFailure.UserNotFound
                : string.Equals(target.Id, administrator.Id, StringComparison.Ordinal)
                    ? ImpersonationStartFailure.SelfImpersonation
                    // Taking over another administrator's session would hand over their platform role.
                    : await userManager.IsInRoleAsync(target, PlatformAdministration.Role)
                        ? ImpersonationStartFailure.TargetIsPlatformAdmin
                        : ImpersonationStartFailure.None;

        await RecordAsync(
            PlatformAdminImpersonation.StartAction,
            administratorUserId,
            targetUserId,
            failure == ImpersonationStartFailure.None ? "succeeded" : "denied",
            cancellationToken);
        return failure == ImpersonationStartFailure.None
            ? new ImpersonationStartResult(new ImpersonationTarget(target!.Id, target.Email ?? target.UserName ?? target.Id), failure)
            : new ImpersonationStartResult(null, failure);
    }

    public async Task<bool> RecordStopAsync(
        string administratorUserId,
        string targetUserId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(administratorUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var administrator = await userManager.FindByIdAsync(administratorUserId);
        var stillAdministrator = administrator is not null
            && await userManager.IsInRoleAsync(administrator, PlatformAdministration.Role);
        await RecordAsync(PlatformAdminImpersonation.StopAction, administratorUserId, targetUserId, reason, cancellationToken);
        return stillAdministrator;
    }

    private async Task RecordAsync(
        string action,
        string administratorUserId,
        string targetUserId,
        string outcome,
        CancellationToken cancellationToken)
    {
        dbContext.PlatformAdminAuditEvents.Add(PlatformAdminAuditEvent.Create(
            action,
            "platform-admin",
            AuditPseudonym.For(targetUserId),
            outcome,
            timeProvider.GetUtcNow(),
            AuditPseudonym.For(administratorUserId)));
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
