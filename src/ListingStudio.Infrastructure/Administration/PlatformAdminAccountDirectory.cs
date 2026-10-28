using ListingStudio.Application.Administration;
using ListingStudio.Infrastructure.Identity;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ListingStudio.Infrastructure.Administration;

public sealed class PlatformAdminAccountDirectory(ApplicationDbContext dbContext) : IPlatformAdminAccountDirectory
{
    private const int MaximumPropertiesPerWorkspace = 50;

    public async Task<AdminAccountSearchResult> SearchAsync(
        string query,
        int maximumResults = 25,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumResults, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumResults, 100);
        var trimmed = (query ?? string.Empty).Trim();
        if (trimmed.Length < IPlatformAdminAccountDirectory.MinimumQueryLength)
        {
            return new AdminAccountSearchResult(trimmed, [], false);
        }

        if (trimmed.Length > 256)
        {
            trimmed = trimmed[..256];
        }

        var pattern = $"%{EscapeLikePattern(trimmed)}%";
        var matchingUsers = dbContext.Users.AsNoTracking()
            .Where(user => EF.Functions.ILike(user.Email ?? string.Empty, pattern, "\\")
                || EF.Functions.ILike(user.UserName ?? string.Empty, pattern, "\\")
                || dbContext.OrganizationMembers.Any(member => member.UserId == user.Id
                    && EF.Functions.ILike(member.Organization.Name, pattern, "\\")));
        return await BuildResultAsync(trimmed, matchingUsers, maximumResults, cancellationToken);
    }

    public async Task<AdminAccountSearchResult> ListTestersAsync(
        int maximumResults = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumResults, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumResults, 500);
        var testers = dbContext.Users.AsNoTracking()
            .Where(user => (
                from userRole in dbContext.UserRoles
                join role in dbContext.Roles on userRole.RoleId equals role.Id
                where role.Name == PlatformTesters.Role
                select userRole.UserId).Contains(user.Id));
        return await BuildResultAsync(string.Empty, testers, maximumResults, cancellationToken);
    }

    private async Task<AdminAccountSearchResult> BuildResultAsync(
        string query,
        IQueryable<ApplicationUser> matchingUsersQuery,
        int maximumResults,
        CancellationToken cancellationToken)
    {
        var matchingUsers = await matchingUsersQuery
            .OrderBy(user => user.Email)
            .Take(maximumResults + 1)
            .Select(user => new
            {
                user.Id,
                Email = user.Email ?? string.Empty,
                UserName = user.UserName ?? string.Empty,
                user.LastSuccessfulSignInAtUtc,
            })
            .ToListAsync(cancellationToken);
        var hasMore = matchingUsers.Count > maximumResults;
        var users = matchingUsers.Take(maximumResults).ToList();
        var userIds = users.Select(user => user.Id).ToList();

        var roleMemberships = await (
                from userRole in dbContext.UserRoles
                join role in dbContext.Roles on userRole.RoleId equals role.Id
                where (role.Name == PlatformAdministration.Role || role.Name == PlatformTesters.Role)
                    && userIds.Contains(userRole.UserId)
                select new { userRole.UserId, role.Name })
            .ToListAsync(cancellationToken);
        var adminUserIds = roleMemberships
            .Where(membership => membership.Name == PlatformAdministration.Role)
            .Select(membership => membership.UserId)
            .ToHashSet(StringComparer.Ordinal);
        var testerUserIds = roleMemberships
            .Where(membership => membership.Name == PlatformTesters.Role)
            .Select(membership => membership.UserId)
            .ToHashSet(StringComparer.Ordinal);
        var memberships = await dbContext.OrganizationMembers.AsNoTracking()
            .Where(member => userIds.Contains(member.UserId))
            .OrderBy(member => member.JoinedAtUtc)
            .Select(member => new
            {
                member.UserId,
                member.OrganizationId,
                member.Organization.Name,
                member.Role,
            })
            .ToListAsync(cancellationToken);
        var organizationIds = memberships.Select(member => member.OrganizationId).Distinct().ToList();
        var properties = await dbContext.Properties.AsNoTracking()
            .Where(property => organizationIds.Contains(property.OrganizationId))
            .OrderBy(property => property.ArchivedAtUtc != null)
            .ThenByDescending(property => property.UpdatedAtUtc)
            .Select(property => new
            {
                property.OrganizationId,
                Row = new AdminPropertyRow(
                    property.Id,
                    property.Address1,
                    property.City,
                    property.State,
                    property.ZipCode,
                    property.ListingStatus,
                    property.ArchivedAtUtc != null,
                    property.UpdatedAtUtc),
            })
            .ToListAsync(cancellationToken);
        var propertiesByOrganization = properties
            .GroupBy(item => item.OrganizationId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<AdminPropertyRow>)group
                    .Select(item => item.Row)
                    .Take(MaximumPropertiesPerWorkspace)
                    .ToList());

        var accounts = users
            .Select(user => new AdminAccountRow(
                user.Id,
                user.Email,
                user.UserName,
                user.LastSuccessfulSignInAtUtc,
                adminUserIds.Contains(user.Id),
                testerUserIds.Contains(user.Id),
                memberships
                    .Where(member => member.UserId == user.Id)
                    .Select(member => new AdminWorkspaceRow(
                        member.OrganizationId,
                        member.Name,
                        member.Role.ToString(),
                        propertiesByOrganization.GetValueOrDefault(member.OrganizationId, [])))
                    .ToList()))
            .ToList();
        return new AdminAccountSearchResult(query, accounts, hasMore);
    }

    private static string EscapeLikePattern(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
