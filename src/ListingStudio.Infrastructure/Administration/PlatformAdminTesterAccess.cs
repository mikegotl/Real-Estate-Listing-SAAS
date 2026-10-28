using ListingStudio.Application.Administration;
using ListingStudio.Domain.Administration;
using ListingStudio.Infrastructure.Identity;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;

namespace ListingStudio.Infrastructure.Administration;

public sealed class PlatformAdminTesterAccess(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole> roleManager,
    ApplicationDbContext dbContext,
    TimeProvider timeProvider) : IPlatformAdminTesterAccess
{
    public async Task<TesterChangeResult> SetTesterAsync(
        string administratorUserId,
        string targetUserId,
        bool isTester,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(administratorUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetUserId);

        var action = isTester ? PlatformTesters.AddAction : PlatformTesters.RemoveAction;
        var administrator = await userManager.FindByIdAsync(administratorUserId);
        if (administrator is null || !await userManager.IsInRoleAsync(administrator, PlatformAdministration.Role))
        {
            await RecordAsync(action, administratorUserId, targetUserId, "denied", cancellationToken);
            return new TesterChangeResult(TesterChangeFailure.NotPlatformAdmin, false, null);
        }

        var target = await userManager.FindByIdAsync(targetUserId);
        if (target is null)
        {
            await RecordAsync(action, administratorUserId, targetUserId, "user-not-found", cancellationToken);
            return new TesterChangeResult(TesterChangeFailure.UserNotFound, false, null);
        }

        var changed = false;
        var welcomeEmailQueued = false;
        if (isTester && !await userManager.IsInRoleAsync(target, PlatformTesters.Role))
        {
            if (!await roleManager.RoleExistsAsync(PlatformTesters.Role))
            {
                EnsureSucceeded(await roleManager.CreateAsync(new IdentityRole(PlatformTesters.Role)));
            }

            EnsureSucceeded(await userManager.AddToRoleAsync(target, PlatformTesters.Role));
            changed = true;
            welcomeEmailQueued = QueueWelcomeEmail(administratorUserId, target);
        }
        else if (!isTester && await userManager.IsInRoleAsync(target, PlatformTesters.Role))
        {
            EnsureSucceeded(await userManager.RemoveFromRoleAsync(target, PlatformTesters.Role));
            changed = true;
        }

        await RecordAsync(
            action,
            administratorUserId,
            targetUserId,
            changed ? (isTester ? "added" : "removed") : "unchanged",
            cancellationToken);
        return new TesterChangeResult(TesterChangeFailure.None, changed, target.Email ?? target.UserName, welcomeEmailQueued);
    }

    // Uses the admin email outbox, so the Worker sends it once SMTP is configured and it shows on Sent emails.
    private bool QueueWelcomeEmail(string administratorUserId, ApplicationUser target)
    {
        var email = target.Email?.Trim();
        if (string.IsNullOrEmpty(email)
            || email.Length > AdminUserEmail.MaximumEmailLength
            || !MimeKit.MailboxAddress.TryParse(email, out _))
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        dbContext.AdminUserEmails.Add(AdminUserEmail.Create(
            Guid.NewGuid(),
            administratorUserId,
            target.Id,
            email,
            PlatformTesters.WelcomeEmailSubject,
            PlatformTesters.WelcomeEmailBody,
            now));
        dbContext.PlatformAdminAuditEvents.Add(PlatformAdminAuditEvent.Create(
            AdminUserEmailAudit.QueueAction,
            "platform-admin",
            AuditPseudonym.For(target.Id),
            "queued",
            now,
            AuditPseudonym.For(administratorUserId)));
        return true;
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

    private static void EnsureSucceeded(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                "Changing the Testers group failed: " + string.Join("; ", result.Errors.Select(error => error.Code)));
        }
    }
}
