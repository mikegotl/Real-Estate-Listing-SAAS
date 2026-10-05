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

        Organization organization;
        try
        {
            organization = Organization.Create(command.OrganizationName);
        }
        catch (ArgumentException exception)
        {
            return AccountRegistrationResult.Failure([exception.Message]);
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var user = new ApplicationUser { UserName = command.Email.Trim(), Email = command.Email.Trim() };
        var identityResult = await userManager.CreateAsync(user, command.Password);

        if (!identityResult.Succeeded)
        {
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
