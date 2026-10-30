namespace ListingStudio.Application.Administration;

/// <summary>
/// Testers are people the Creative Listing Studio team lets use the site for free. Membership is an Identity role that only
/// platform administrators can grant or remove from Find users; every change is written to the audit log.
/// </summary>
public static class PlatformTesters
{
    public const string Role = "Tester";

    public const string AddAction = "platform-admin.testers.add";
    public const string RemoveAction = "platform-admin.testers.remove";

    /// <summary>Queued automatically, from the admin who added them, when someone joins the Testers group.</summary>
    public const string WelcomeEmailSubject = "You're now a Creative Listing Studio tester";

    public const string WelcomeEmailBody = """
        Hi,

        You've been added to the Creative Listing Studio Testers group, so you can use Creative Listing Studio for free while you help us test it. Campaigns you create don't need a plan and don't count toward your workspace's allowance.

        Sign in as usual to get started. Reply to this email with anything that feels off or could be better.

        Thanks for helping,
        The Creative Listing Studio team
        """;
}

public enum TesterChangeFailure
{
    None = 0,
    NotPlatformAdmin = 1,
    UserNotFound = 2,
}

/// <param name="WelcomeEmailQueued">True when the user was added and has an address, so a welcome email was queued.</param>
public sealed record TesterChangeResult(
    TesterChangeFailure Failure,
    bool Changed,
    string? UserEmail,
    bool WelcomeEmailQueued = false)
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
