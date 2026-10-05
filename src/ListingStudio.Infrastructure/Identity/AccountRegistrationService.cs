using ListingStudio.Application.Authentication;
using ListingStudio.Domain.Organizations;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ListingStudio.Domain.Billing;

namespace ListingStudio.Infrastructure.Identity;

internal sealed class AccountRegistrationService(
    ApplicationDbContext dbContext,
    UserManager<ApplicationUser> userManager,
    TimeProvider timeProvider) : IAccountRegistrationService
{
    public async Task<AccountRegistrationResult> RegisterAsync(
        RegisterAccountCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        return await RegisterCoreAsync(
            command.Email,
            command.OrganizationName,
            user => userManager.CreateAsync(user, command.Password),
            cancellationToken);
    }

    public async Task<AccountRegistrationResult> RegisterExternalAsync(
        RegisterExternalAccountCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.LoginProvider)
            || string.IsNullOrWhiteSpace(command.ProviderKey)
            || string.IsNullOrWhiteSpace(command.ProviderDisplayName))
        {
            return AccountRegistrationResult.Failure(["External login information is incomplete."]);
        }

        return await RegisterCoreAsync(
            command.Email,
            command.OrganizationName,
            async user =>
            {
                var created = await userManager.CreateAsync(user);
                if (!created.Succeeded)
                {
                    return created;
                }

                return await userManager.AddLoginAsync(
                    user,
                    new UserLoginInfo(
                        command.LoginProvider,
                        command.ProviderKey,
                        command.ProviderDisplayName));
            },
            cancellationToken);
    }

    private async Task<AccountRegistrationResult> RegisterCoreAsync(
        string email,
        string organizationName,
        Func<ApplicationUser, Task<IdentityResult>> createUser,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return AccountRegistrationResult.Failure(["An email address is required."]);
        }

        Organization organization;
        try
        {
            organization = Organization.Create(organizationName);
        }
        catch (ArgumentException exception)
        {
            return AccountRegistrationResult.Failure([exception.Message]);
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var normalizedEmail = email.Trim();
        var user = new ApplicationUser { UserName = normalizedEmail, Email = normalizedEmail };
        var identityResult = await createUser(user);

        if (!identityResult.Succeeded)
        {
            await transaction.RollbackAsync(cancellationToken);
            return AccountRegistrationResult.Failure(identityResult.Errors.Select(error => error.Description));
        }

        dbContext.Organizations.Add(organization);
        dbContext.OrganizationMembers.Add(OrganizationMember.CreateOwner(organization.Id, user.Id));
        dbContext.OrganizationBillingAccounts.Add(
            OrganizationBillingAccount.Create(organization.Id, timeProvider.GetUtcNow()));

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return AccountRegistrationResult.Success(user.Id, organization.Id);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }
}
