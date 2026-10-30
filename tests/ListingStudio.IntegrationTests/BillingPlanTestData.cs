using ListingStudio.Domain.Billing;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ListingStudio.IntegrationTests;

/// <summary>Puts a test user's workspace on an active paid plan, as a verified Stripe webhook would.</summary>
internal static class BillingPlanTestData
{
    public static async Task SubscribeAsync(
        IServiceProvider services,
        string userId,
        SubscriptionPlan plan,
        DateTimeOffset now,
        BillingInterval interval = BillingInterval.Monthly)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var organizationId = await dbContext.OrganizationMembers
            .Where(member => member.UserId == userId)
            .Select(member => member.OrganizationId)
            .FirstAsync();
        var account = await dbContext.OrganizationBillingAccounts
            .SingleOrDefaultAsync(candidate => candidate.OrganizationId == organizationId);
        if (account is null)
        {
            account = OrganizationBillingAccount.Create(organizationId, now);
            dbContext.OrganizationBillingAccounts.Add(account);
        }

        account.ApplySubscription(
            $"cus_test_{organizationId:N}",
            $"sub_test_{organizationId:N}",
            plan,
            BillingSubscriptionStatus.Active,
            now.AddDays(-1),
            interval == BillingInterval.Annual ? now.AddDays(-1).AddYears(1) : now.AddDays(-1).AddMonths(1),
            plan == SubscriptionPlan.Enterprise ? 30 : plan == SubscriptionPlan.Professional ? 12 : 4,
            now,
            now,
            interval);
        await dbContext.SaveChangesAsync();
    }
}
