using System.Security.Cryptography;
using System.Text;
using ListingStudio.Application.Administration;
using ListingStudio.Domain.Administration;
using ListingStudio.Infrastructure.Identity;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ListingStudio.Infrastructure.Administration;

public sealed class PlatformAdminRoleBootstrapper(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole> roleManager,
    ApplicationDbContext dbContext,
    TimeProvider timeProvider)
{
    public async Task<bool> GrantAsync(string email, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        var normalized = email.Trim();
        var user = await userManager.FindByEmailAsync(normalized)
            ?? throw new InvalidOperationException("No existing account matches the supplied email address.");
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        if (!await roleManager.RoleExistsAsync(PlatformAdministration.Role))
        {
            var roleResult = await roleManager.CreateAsync(new IdentityRole(PlatformAdministration.Role));
            EnsureSucceeded(roleResult);
        }

        var added = false;
        if (!await userManager.IsInRoleAsync(user, PlatformAdministration.Role))
        {
            EnsureSucceeded(await userManager.AddToRoleAsync(user, PlatformAdministration.Role));
            added = true;
        }

        dbContext.PlatformAdminAuditEvents.Add(PlatformAdminAuditEvent.Create(
            "platform-admin.grant",
            "controlled-command",
            Pseudonym(user.Id),
            added ? "succeeded" : "unchanged",
            timeProvider.GetUtcNow()));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return added;
    }

    public async Task<bool> RevokeAsync(string email, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        var user = await userManager.FindByEmailAsync(email.Trim())
            ?? throw new InvalidOperationException("No existing account matches the supplied email address.");
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var removed = false;
        if (await userManager.IsInRoleAsync(user, PlatformAdministration.Role))
        {
            EnsureSucceeded(await userManager.RemoveFromRoleAsync(user, PlatformAdministration.Role));
            removed = true;
        }

        dbContext.PlatformAdminAuditEvents.Add(PlatformAdminAuditEvent.Create(
            "platform-admin.revoke",
            "controlled-command",
            Pseudonym(user.Id),
            removed ? "succeeded" : "unchanged",
            timeProvider.GetUtcNow()));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return removed;
    }

    private static string Pseudonym(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void EnsureSucceeded(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(string.Join("; ", result.Errors.Select(error => error.Description)));
        }
    }
}
