namespace ListingStudio.Application.Administration;

/// <summary>
/// Testers are people the Listing Studio team lets use the site for free. Membership is an Identity role that only
/// platform administrators can grant or remove from Find users; every change is written to the audit log.
/// </summary>
public static class PlatformTesters
{
    public const string Role = "Tester";

    public const string AddAction = "platform-admin.testers.add";
    public const string RemoveAction = "platform-admin.testers.remove";
}

public enum TesterChangeFailure
{
    None = 0,
    NotPlatformAdmin = 1,
    UserNotFound = 2,
}

public sealed record TesterChangeResult(TesterChangeFailure Failure, bool Changed, string? UserEmail)
{
    public bool Succeeded => Failure == TesterChangeFailure.None;
}

public interface IPlatformAdminTesterAccess
{
    /// <summary>
    /// Adds the user to, or removes them from, the Testers group and records the attempt in the audit log, whether
    /// or not it is allowed.
    /// </summary>
    Task<TesterChangeResult> SetTesterAsync(
        string administratorUserId,
        string targetUserId,
        bool isTester,
        CancellationToken cancellationToken = default);
}
