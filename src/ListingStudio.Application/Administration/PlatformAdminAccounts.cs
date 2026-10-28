using System.Security.Claims;
using ListingStudio.Domain.Properties;

namespace ListingStudio.Application.Administration;

public sealed record AdminAccountSearchResult(
    string Query,
    IReadOnlyList<AdminAccountRow> Accounts,
    bool HasMore);

public sealed record AdminAccountRow(
    string UserId,
    string Email,
    string UserName,
    DateTimeOffset? LastSuccessfulSignInAtUtc,
    bool IsPlatformAdmin,
    bool IsTester,
    IReadOnlyList<AdminWorkspaceRow> Workspaces);

public sealed record AdminWorkspaceRow(
    Guid OrganizationId,
    string Name,
    string Role,
    IReadOnlyList<AdminPropertyRow> Properties);

public sealed record AdminPropertyRow(
    Guid Id,
    string Address1,
    string City,
    string State,
    string ZipCode,
    ListingStatus ListingStatus,
    bool IsArchived,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// Lets platform administrators find customer accounts by email, user name or workspace name. Results are for
/// support navigation only: opening a property still goes through an audited impersonation session, so the admin
/// role itself never reads tenant data through the normal property services.
/// </summary>
public interface IPlatformAdminAccountDirectory
{
    public const int MinimumQueryLength = 2;

    Task<AdminAccountSearchResult> SearchAsync(
        string query,
        int maximumResults = 25,
        CancellationToken cancellationToken = default);

    /// <summary>Lists every user in the Testers group, ordered by email.</summary>
    Task<AdminAccountSearchResult> ListTestersAsync(
        int maximumResults = 100,
        CancellationToken cancellationToken = default);
}

public static class PlatformAdminImpersonation
{
    /// <summary>The platform administrator's user id, carried on an impersonation session's cookie.</summary>
    public const string ImpersonatorUserIdClaim = "listing-studio:impersonator-id";

    /// <summary>The platform administrator's email, shown in the impersonation banner.</summary>
    public const string ImpersonatorEmailClaim = "listing-studio:impersonator-email";

    /// <summary>Impersonation sessions end on their own after this long and cannot be extended.</summary>
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromHours(1);

    public const string StartAction = "platform-admin.impersonation.start";
    public const string StopAction = "platform-admin.impersonation.stop";

    /// <summary>The administrator behind an impersonation session, or null for a normal session.</summary>
    public static string? ImpersonatorUserId(ClaimsPrincipal user) =>
        user.FindFirstValue(ImpersonatorUserIdClaim);

    public static bool IsImpersonating(ClaimsPrincipal user) => ImpersonatorUserId(user) is not null;
}

public sealed record ImpersonationTarget(string UserId, string Email);

public enum ImpersonationStartFailure
{
    None = 0,
    NotPlatformAdmin = 1,
    UserNotFound = 2,
    SelfImpersonation = 3,
    TargetIsPlatformAdmin = 4,
}

public sealed record ImpersonationStartResult(ImpersonationTarget? Target, ImpersonationStartFailure Failure)
{
    public bool Succeeded => Failure == ImpersonationStartFailure.None && Target is not null;
}

public interface IPlatformAdminImpersonationAudit
{
    /// <summary>
    /// Checks that the administrator may impersonate the target and records the attempt in the append-only audit
    /// log, whether or not it is allowed.
    /// </summary>
    Task<ImpersonationStartResult> AuthorizeStartAsync(
        string administratorUserId,
        string targetUserId,
        CancellationToken cancellationToken = default);

    /// <summary>Records the end of an impersonation session. Returns true when the administrator still holds the platform role.</summary>
    Task<bool> RecordStopAsync(
        string administratorUserId,
        string targetUserId,
        string reason,
        CancellationToken cancellationToken = default);
}
