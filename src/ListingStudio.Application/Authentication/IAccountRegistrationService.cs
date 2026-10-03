namespace ListingStudio.Application.Authentication;

public interface IAccountRegistrationService
{
    Task<AccountRegistrationResult> RegisterAsync(
        RegisterAccountCommand command,
        CancellationToken cancellationToken = default);
}

public sealed record RegisterAccountCommand(string Email, string Password, string OrganizationName);

public sealed record AccountRegistrationResult(
    bool Succeeded,
    string? UserId,
    Guid? OrganizationId,
    IReadOnlyCollection<string> Errors)
{
    public static AccountRegistrationResult Success(string userId, Guid organizationId) =>
        new(true, userId, organizationId, []);

    public static AccountRegistrationResult Failure(IEnumerable<string> errors) =>
        new(false, null, null, errors.ToArray());
}
