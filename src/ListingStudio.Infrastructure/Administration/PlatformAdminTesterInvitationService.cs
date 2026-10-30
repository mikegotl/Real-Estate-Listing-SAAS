using System.Security.Cryptography;
using ListingStudio.Application.Administration;
using ListingStudio.Domain.Administration;
using ListingStudio.Domain.Billing;
using ListingStudio.Domain.Organizations;
using ListingStudio.Infrastructure.Configuration;
using ListingStudio.Infrastructure.Identity;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using MimeKit;

namespace ListingStudio.Infrastructure.Administration;

public sealed class PlatformAdminTesterInvitationService(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole> roleManager,
    ApplicationDbContext dbContext,
    TimeProvider timeProvider,
    IOptions<StripeOptions> stripeOptions,
    IOptions<TesterInvitationOptions> invitationOptions,
    IDataProtectionProvider dataProtectionProvider) : IPlatformAdminTesterInvitationService
{
    public async Task<TesterInvitationResult> InviteAsync(
        string administratorUserId,
        string email,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(administratorUserId);

        var administrator = await userManager.FindByIdAsync(administratorUserId);
        if (administrator is null || !await userManager.IsInRoleAsync(administrator, PlatformAdministration.Role))
        {
            await RecordAsync(administratorUserId, email, "denied", cancellationToken);
            return TesterInvitationResult.Failed(TesterInvitationFailure.NotPlatformAdmin);
        }

        var normalizedEmail = (email ?? string.Empty).Trim();
        if (normalizedEmail.Length == 0
            || normalizedEmail.Length > AdminUserEmail.MaximumEmailLength
            || !MailboxAddress.TryParse(normalizedEmail, out var mailbox)
            || !string.Equals(mailbox.Address, normalizedEmail, StringComparison.OrdinalIgnoreCase))
        {
            await RecordAsync(administratorUserId, normalizedEmail, "invalid-email", cancellationToken);
            return TesterInvitationResult.Failed(
                TesterInvitationFailure.InvalidEmail,
                "Enter a valid email address.");
        }

        normalizedEmail = mailbox.Address;
        var existing = await userManager.FindByEmailAsync(normalizedEmail);
        if (existing is not null)
        {
            return await InviteExistingAsync(administratorUserId, existing, cancellationToken);
        }

        return await CreateInvitedAccountAsync(administratorUserId, normalizedEmail, cancellationToken);
    }

    private async Task<TesterInvitationResult> InviteExistingAsync(
        string administratorUserId,
        ApplicationUser user,
        CancellationToken cancellationToken)
    {
        if (await userManager.IsInRoleAsync(user, PlatformTesters.Role))
        {
            await RecordAsync(administratorUserId, user.Id, "already-tester", cancellationToken);
            return new TesterInvitationResult(
                TesterInvitationFailure.None, false, false, false, user.Email ?? user.UserName, []);
        }

        await EnsureTesterRoleAsync();
        var roleResult = await userManager.AddToRoleAsync(user, PlatformTesters.Role);
        if (!roleResult.Succeeded)
        {
            return await FailedCreationAsync(administratorUserId, user.Id, roleResult.Errors, cancellationToken);
        }

        var emailQueued = QueueWelcomeEmail(
            administratorUserId,
            user,
            ExistingAccountEmailBody(user.Email ?? user.UserName ?? string.Empty),
            bodyIsProtected: false);
        await RecordAsync(administratorUserId, user.Id, "existing-account-added", cancellationToken);
        return new TesterInvitationResult(
            TesterInvitationFailure.None, false, true, emailQueued, user.Email ?? user.UserName, []);
    }

    private async Task<TesterInvitationResult> CreateInvitedAccountAsync(
        string administratorUserId,
        string email,
        CancellationToken cancellationToken)
    {
        var temporaryPassword = GenerateTemporaryPassword();
        var user = new ApplicationUser { UserName = email, Email = email };
        user.RequirePasswordChange();
        var organization = Organization.Create(WorkspaceName(email));

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var createResult = await userManager.CreateAsync(user, temporaryPassword);
        if (!createResult.Succeeded)
        {
            await transaction.RollbackAsync(cancellationToken);
            await transaction.DisposeAsync();
            dbContext.ChangeTracker.Clear();
            return await FailedCreationAsync(administratorUserId, email, createResult.Errors, cancellationToken);
        }

        dbContext.Organizations.Add(organization);
        dbContext.OrganizationMembers.Add(OrganizationMember.CreateOwner(organization.Id, user.Id));
        dbContext.OrganizationBillingAccounts.Add(OrganizationBillingAccount.Create(
            organization.Id,
            timeProvider.GetUtcNow(),
            stripeOptions.Value.FreeTrialCampaignAllowance,
            stripeOptions.Value.FreeTrialDays));

        await EnsureTesterRoleAsync();
        var roleResult = await userManager.AddToRoleAsync(user, PlatformTesters.Role);
        if (!roleResult.Succeeded)
        {
            await transaction.RollbackAsync(cancellationToken);
            await transaction.DisposeAsync();
            dbContext.ChangeTracker.Clear();
            return await FailedCreationAsync(administratorUserId, email, roleResult.Errors, cancellationToken);
        }

        var body = NewAccountEmailBody(email, temporaryPassword);
        var protectedBody = TesterInvitationEmailProtection.CreateProtector(dataProtectionProvider).Protect(body);
        var emailQueued = QueueWelcomeEmail(administratorUserId, user, protectedBody, bodyIsProtected: true);
        dbContext.PlatformAdminAuditEvents.Add(PlatformAdminAuditEvent.Create(
            PlatformTesters.InviteAction,
            "platform-admin",
            AuditPseudonym.For(user.Id),
            "account-created",
            timeProvider.GetUtcNow(),
            AuditPseudonym.For(administratorUserId)));

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await transaction.DisposeAsync();
        return new TesterInvitationResult(
            TesterInvitationFailure.None, true, true, emailQueued, email, []);
    }

    private async Task EnsureTesterRoleAsync()
    {
        if (!await roleManager.RoleExistsAsync(PlatformTesters.Role))
        {
            var result = await roleManager.CreateAsync(new IdentityRole(PlatformTesters.Role));
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(
                    "Creating the Testers role failed: " + string.Join("; ", result.Errors.Select(error => error.Code)));
            }
        }
    }

    private bool QueueWelcomeEmail(
        string administratorUserId,
        ApplicationUser user,
        string body,
        bool bodyIsProtected)
    {
        var email = user.Email?.Trim();
        if (string.IsNullOrEmpty(email))
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        dbContext.AdminUserEmails.Add(AdminUserEmail.Create(
            Guid.NewGuid(),
            administratorUserId,
            user.Id,
            email,
            PlatformTesters.WelcomeEmailSubject,
            body,
            now,
            bodyIsProtected));
        dbContext.PlatformAdminAuditEvents.Add(PlatformAdminAuditEvent.Create(
            AdminUserEmailAudit.QueueAction,
            "platform-admin",
            AuditPseudonym.For(user.Id),
            "queued",
            now,
            AuditPseudonym.For(administratorUserId)));
        return true;
    }

    private string ExistingAccountEmailBody(string email) => $"""
        Hi,

        You've been added to the Creative Listing Studio Testers group, so you can use Creative Listing Studio for free while you help us test it.

        Sign in: {LoginUrl()}
        Username: {email}

        Use your existing password or your linked sign-in provider. Reply to this email with anything that feels off or could be better.

        Thanks for helping,
        The Creative Listing Studio team
        """;

    private string NewAccountEmailBody(string email, string temporaryPassword) => $"""
        Hi,

        You've been invited to test Creative Listing Studio. A private tester workspace has been created for you, and your campaigns can run without a paid plan while you help us test.

        Sign in: {LoginUrl()}
        Username: {email}
        Temporary password: {temporaryPassword}

        The first time you sign in, you must replace this temporary password before you can use the site. Do not share it with anyone.

        Thanks for helping,
        The Creative Listing Studio team
        """;

    private string LoginUrl()
    {
        var configured = invitationOptions.Value.AppBaseUrl?.Trim();
        var baseUrl = Uri.TryCreate(configured, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority).TrimEnd('/')
            : "https://creativelistingstudio.com";
        return $"{baseUrl}/Account/Login";
    }

    private static string WorkspaceName(string email)
    {
        var local = email.Split('@', 2)[0].Trim();
        if (local.Length == 0)
        {
            return "Tester workspace";
        }

        var maximumLocalLength = 190;
        return $"{local[..Math.Min(local.Length, maximumLocalLength)]}'s workspace";
    }

    private static string GenerateTemporaryPassword() =>
        $"CLS!{Convert.ToHexString(RandomNumberGenerator.GetBytes(10))}a9";

    private async Task<TesterInvitationResult> FailedCreationAsync(
        string administratorUserId,
        string target,
        IEnumerable<IdentityError> errors,
        CancellationToken cancellationToken)
    {
        var messages = errors.Select(error => error.Description).ToArray();
        await RecordAsync(administratorUserId, target, "account-creation-failed", cancellationToken);
        return TesterInvitationResult.Failed(TesterInvitationFailure.AccountCreationFailed, messages);
    }

    private async Task RecordAsync(
        string administratorUserId,
        string target,
        string outcome,
        CancellationToken cancellationToken)
    {
        dbContext.PlatformAdminAuditEvents.Add(PlatformAdminAuditEvent.Create(
            PlatformTesters.InviteAction,
            "platform-admin",
            AuditPseudonym.For(target),
            outcome,
            timeProvider.GetUtcNow(),
            AuditPseudonym.For(administratorUserId)));
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
