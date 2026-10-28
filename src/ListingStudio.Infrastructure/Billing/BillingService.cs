namespace ListingStudio.Infrastructure.Billing;

using System.Data;
using ListingStudio.Application.Administration;
using ListingStudio.Application.Billing;
using ListingStudio.Domain.Billing;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Infrastructure.Configuration;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

public sealed class BillingService(
    ApplicationDbContext dbContext,
    IBillingProviderGateway provider,
    IOptions<StripeOptions> options,
    TimeProvider timeProvider) : IBillingService, ICampaignBillingGate
{
    public async Task<BillingOverview> GetOverviewAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var identity = await GetIdentityAsync(userId, cancellationToken);
        var account = await GetOrCreateAccountAsync(identity.OrganizationId, cancellationToken);
        account.ExpireFreeTrial(timeProvider.GetUtcNow());
        var reservedCampaignUsage = await ReservedCampaignUsageAsync(account, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToOverview(account, identity.OrganizationName, reservedCampaignUsage, timeProvider.GetUtcNow()) with
        {
            IsTester = await IsTesterAsync(userId, cancellationToken),
        };
    }

    public async Task<BillingRedirect> CreateCheckoutSessionAsync(
        string userId,
        SubscriptionPlan plan,
        string successUrl,
        string cancelUrl,
        CancellationToken cancellationToken = default)
    {
        if (plan is not (SubscriptionPlan.Starter or SubscriptionPlan.Professional))
        {
            throw new ArgumentOutOfRangeException(nameof(plan), "A paid subscription plan is required.");
        }

        EnsureAbsoluteUrl(successUrl, nameof(successUrl));
        EnsureAbsoluteUrl(cancelUrl, nameof(cancelUrl));
        var identity = await GetIdentityAsync(userId, cancellationToken);
        var account = await GetOrCreateAccountAsync(identity.OrganizationId, cancellationToken);
        if (account.StripeCustomerId is null)
        {
            var customerId = await provider.CreateCustomerAsync(
                new BillingProviderCustomerRequest(
                    identity.OrganizationId,
                    identity.OrganizationName,
                    identity.OwnerEmail,
                    $"customer:{identity.OrganizationId:D}"),
                cancellationToken);
            account.LinkCustomer(customerId, timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var stripeCustomerId = account.StripeCustomerId
            ?? throw new InvalidOperationException("The Stripe customer was not persisted.");
        return await provider.CreateCheckoutSessionAsync(
            new BillingProviderCheckoutRequest(
                identity.OrganizationId,
                stripeCustomerId,
                plan,
                PriceId(plan),
                successUrl,
                cancelUrl,
                $"checkout:{identity.OrganizationId:D}:{plan}"),
            cancellationToken);
    }

    public async Task<BillingRedirect> CreatePortalSessionAsync(
        string userId,
        string returnUrl,
        CancellationToken cancellationToken = default)
    {
        EnsureAbsoluteUrl(returnUrl, nameof(returnUrl));
        var identity = await GetIdentityAsync(userId, cancellationToken);
        var account = await GetOrCreateAccountAsync(identity.OrganizationId, cancellationToken);
        if (account.StripeCustomerId is null)
        {
            throw new InvalidOperationException("A Stripe customer must exist before opening the billing portal.");
        }

        return await provider.CreatePortalSessionAsync(
            new BillingProviderPortalRequest(
                account.StripeCustomerId,
                returnUrl,
                $"portal:{identity.OrganizationId:D}:{timeProvider.GetUtcNow():yyyyMMddHHmm}"),
            cancellationToken);
    }

    public async Task<BillingWebhookOutcome> ProcessWebhookAsync(
        string payload,
        string signatureHeader,
        CancellationToken cancellationToken = default)
    {
        var parsed = provider.ParseAndVerifyWebhook(payload, signatureHeader);
        var envelope = parsed switch
        {
            BillingProviderSubscriptionEvent value => (value.EventId, value.EventType, value.EventCreatedUtc, value.PayloadSha256),
            BillingProviderCheckoutCompletedEvent value => (value.EventId, value.EventType, value.EventCreatedUtc, value.PayloadSha256),
            BillingProviderIgnoredEvent value => (value.EventId, value.EventType, value.EventCreatedUtc, value.PayloadSha256),
            _ => throw new InvalidOperationException("The billing provider returned an unsupported webhook event."),
        };

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var inserted = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "BillingWebhookReceipts"
                ("EventId", "EventType", "PayloadSha256", "EventCreatedUtc", "ProcessedAtUtc")
            VALUES
                ({envelope.EventId}, {envelope.EventType}, {envelope.PayloadSha256},
                 {envelope.EventCreatedUtc}, {timeProvider.GetUtcNow()})
            ON CONFLICT ("EventId") DO NOTHING
            """, cancellationToken);
        if (inserted == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return BillingWebhookOutcome.AlreadyProcessed;
        }

        var outcome = parsed switch
        {
            BillingProviderSubscriptionEvent subscription => await ApplySubscriptionAsync(subscription, cancellationToken),
            BillingProviderCheckoutCompletedEvent checkout => await ApplyCheckoutAsync(checkout, cancellationToken),
            BillingProviderIgnoredEvent => BillingWebhookOutcome.Ignored,
            _ => throw new InvalidOperationException("The billing provider returned an unsupported webhook event."),
        };
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return outcome;
    }

    public async Task EnsureCampaignCapacityAsync(
        Guid organizationId,
        Guid propertyId,
        string requestedByUserId,
        CancellationToken cancellationToken = default)
    {
        if (propertyId == Guid.Empty)
        {
            throw new ArgumentException("A property identity is required.", nameof(propertyId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(requestedByUserId);
        // Testers use the site for free: no plan, trial or allowance is needed and nothing is reserved.
        if (await IsTesterAsync(requestedByUserId, cancellationToken))
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var account = await GetLockedAccountAsync(organizationId, cancellationToken);
        account.ExpireFreeTrial(now);

        var isTrial = account.Plan == SubscriptionPlan.Free;
        var activeSubscription = account.Plan is SubscriptionPlan.Starter or SubscriptionPlan.Professional
            && account.SubscriptionStatus is BillingSubscriptionStatus.Active or BillingSubscriptionStatus.Trialing
            && now < account.CurrentPeriodEndUtc;
        var activeFreeTrial = isTrial
            && account.MonthlyCampaignAllowance > 0
            && now < account.CurrentPeriodEndUtc;
        if (!activeFreeTrial && !activeSubscription)
        {
            throw new CampaignAllowanceExceededException(isTrial
                ? "Your free campaign trial has ended. Choose a plan on Billing to create another campaign."
                : "Your subscription is not currently active. Review it on Billing before creating another campaign.");
        }

        if (await PropertyCampaignUsageExistsAsync(account, propertyId, cancellationToken))
        {
            return;
        }

        var reservedCampaignUsage = await ReservedCampaignUsageAsync(account, cancellationToken);
        if (account.CampaignUsage + reservedCampaignUsage >= account.MonthlyCampaignAllowance)
        {
            throw new CampaignAllowanceExceededException(isTrial
                ? "Your free campaign allowance is fully used or another campaign is still processing. Choose a plan on Billing to create another campaign."
                : $"Your {account.Plan} plan's campaign allowance is fully used or reserved for this period. Upgrade on Billing to continue.");
        }
    }

    public async Task RecordCompletedCampaignAsync(
        Guid organizationId,
        Guid campaignGenerationJobId,
        CancellationToken cancellationToken = default)
    {
        var exists = await dbContext.CampaignUsageRecords
            .AnyAsync(record => record.CampaignGenerationJobId == campaignGenerationJobId, cancellationToken);
        if (exists)
        {
            return;
        }

        var job = await dbContext.CampaignGenerationJobs.AsNoTracking()
            .Where(job => job.Id == campaignGenerationJobId && job.OrganizationId == organizationId)
            .Select(job => new { job.PropertyId, job.RequestedByUserId })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("The completed campaign does not belong to the billing organization.");
        if (await IsTesterAsync(job.RequestedByUserId, cancellationToken))
        {
            return;
        }

        var account = await GetLockedAccountAsync(organizationId, cancellationToken);
        if (await PropertyCampaignUsageExistsAsync(account, job.PropertyId, cancellationToken))
        {
            return;
        }

        account.RecordCampaignUsage(timeProvider.GetUtcNow());
        dbContext.CampaignUsageRecords.Add(CampaignUsageRecord.Create(
            organizationId,
            campaignGenerationJobId,
            account.CurrentPeriodStartUtc,
            account.AdditionalCampaignUsage > 0,
            timeProvider.GetUtcNow()));
    }

    private async Task<BillingWebhookOutcome> ApplySubscriptionAsync(
        BillingProviderSubscriptionEvent subscription,
        CancellationToken cancellationToken)
    {
        var account = await dbContext.OrganizationBillingAccounts
            .FromSqlInterpolated($"""
                SELECT * FROM "OrganizationBillingAccounts"
                WHERE "StripeCustomerId" = {subscription.StripeCustomerId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("The Stripe customer is not linked to a Listing Studio organization.");
        var plan = Plan(subscription.PriceId);
        account.ApplySubscription(
            subscription.StripeCustomerId,
            subscription.StripeSubscriptionId,
            plan,
            Status(subscription.Status),
            subscription.CurrentPeriodStartUtc,
            subscription.CurrentPeriodEndUtc,
            Allowance(plan),
            subscription.EventCreatedUtc,
            timeProvider.GetUtcNow());
        return BillingWebhookOutcome.Processed;
    }

    private async Task<BillingWebhookOutcome> ApplyCheckoutAsync(
        BillingProviderCheckoutCompletedEvent checkout,
        CancellationToken cancellationToken)
    {
        var account = await dbContext.OrganizationBillingAccounts
            .FromSqlInterpolated($"""
                SELECT * FROM "OrganizationBillingAccounts"
                WHERE "OrganizationId" = {checkout.OrganizationId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("The checkout organization does not exist.");
        account.LinkCustomer(checkout.StripeCustomerId, timeProvider.GetUtcNow());
        account.LinkSubscription(checkout.StripeSubscriptionId, timeProvider.GetUtcNow());
        return BillingWebhookOutcome.Processed;
    }

    private async Task<BillingIdentity> GetIdentityAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var identity = await (
            from member in dbContext.OrganizationMembers.AsNoTracking()
            join organization in dbContext.Organizations.AsNoTracking()
                on member.OrganizationId equals organization.Id
            join user in dbContext.Users.AsNoTracking()
                on member.UserId equals user.Id
            where member.UserId == userId
            select new
            {
                OrganizationId = organization.Id,
                OrganizationName = organization.Name,
                OwnerEmail = user.Email,
            })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new UnauthorizedAccessException("The user does not belong to an organization.");
        return new BillingIdentity(
            identity.OrganizationId,
            identity.OrganizationName,
            identity.OwnerEmail ?? throw new InvalidOperationException("The billing owner has no email address."));
    }

    private async Task<OrganizationBillingAccount> GetOrCreateAccountAsync(
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        var account = await dbContext.OrganizationBillingAccounts
            .SingleOrDefaultAsync(candidate => candidate.OrganizationId == organizationId, cancellationToken);
        if (account is not null)
        {
            return account;
        }

        account = CreateTrialAccount(organizationId);
        dbContext.OrganizationBillingAccounts.Add(account);
        return account;
    }

    private async Task<OrganizationBillingAccount> GetLockedAccountAsync(
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        var account = await dbContext.OrganizationBillingAccounts
            .FromSqlInterpolated($"""
                SELECT * FROM "OrganizationBillingAccounts"
                WHERE "OrganizationId" = {organizationId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);
        if (account is not null)
        {
            return account;
        }

        account = CreateTrialAccount(organizationId);
        dbContext.OrganizationBillingAccounts.Add(account);
        await dbContext.SaveChangesAsync(cancellationToken);
        return account;
    }

    private OrganizationBillingAccount CreateTrialAccount(Guid organizationId) =>
        OrganizationBillingAccount.Create(
            organizationId,
            timeProvider.GetUtcNow(),
            options.Value.FreeTrialCampaignAllowance,
            options.Value.FreeTrialDays);

    private async Task<int> ReservedCampaignUsageAsync(
        OrganizationBillingAccount account,
        CancellationToken cancellationToken) =>
        await dbContext.CampaignGenerationJobs.AsNoTracking().CountAsync(job =>
            job.OrganizationId == account.OrganizationId
            && job.CreatedAtUtc >= account.CurrentPeriodStartUtc
            && (job.Status == CampaignGenerationStatus.Queued
                || job.Status == CampaignGenerationStatus.Running)
            && !TesterUserIds().Contains(job.RequestedByUserId)
            && !dbContext.CampaignUsageRecords.Any(record =>
                record.CampaignGenerationJobId == job.Id)
            && !dbContext.CampaignUsageRecords.Any(record =>
                record.OrganizationId == account.OrganizationId
                && record.PeriodStartUtc == account.CurrentPeriodStartUtc
                && record.CampaignGenerationJob.PropertyId == job.PropertyId),
            cancellationToken);

    private IQueryable<string> TesterUserIds() =>
        from userRole in dbContext.UserRoles
        join role in dbContext.Roles on userRole.RoleId equals role.Id
        where role.Name == PlatformTesters.Role
        select userRole.UserId;

    private Task<bool> IsTesterAsync(string userId, CancellationToken cancellationToken) =>
        TesterUserIds().AnyAsync(testerId => testerId == userId, cancellationToken);

    private async Task<bool> PropertyCampaignUsageExistsAsync(
        OrganizationBillingAccount account,
        Guid propertyId,
        CancellationToken cancellationToken) =>
        await dbContext.CampaignUsageRecords.AsNoTracking().AnyAsync(record =>
            record.OrganizationId == account.OrganizationId
            && record.PeriodStartUtc == account.CurrentPeriodStartUtc
            && record.CampaignGenerationJob.PropertyId == propertyId,
            cancellationToken);

    private string PriceId(SubscriptionPlan plan)
    {
        var priceId = plan switch
        {
            SubscriptionPlan.Starter => options.Value.StarterPriceId,
            SubscriptionPlan.Professional => options.Value.ProfessionalPriceId,
            _ => string.Empty,
        };
        return !string.IsNullOrWhiteSpace(priceId)
            ? priceId
            : throw new InvalidOperationException($"Stripe price configuration is missing for the {plan} plan.");
    }

    private SubscriptionPlan Plan(string priceId)
    {
        if (string.Equals(priceId, options.Value.StarterPriceId, StringComparison.Ordinal))
        {
            return SubscriptionPlan.Starter;
        }

        if (string.Equals(priceId, options.Value.ProfessionalPriceId, StringComparison.Ordinal))
        {
            return SubscriptionPlan.Professional;
        }

        throw new InvalidOperationException("The Stripe subscription uses an unknown price.");
    }

    private int Allowance(SubscriptionPlan plan) => plan switch
    {
        SubscriptionPlan.Starter => options.Value.StarterMonthlyCampaignAllowance,
        SubscriptionPlan.Professional => options.Value.ProfessionalMonthlyCampaignAllowance,
        _ => 0,
    };

    private static BillingSubscriptionStatus Status(string value) => value switch
    {
        "incomplete" => BillingSubscriptionStatus.Incomplete,
        "incomplete_expired" => BillingSubscriptionStatus.IncompleteExpired,
        "trialing" => BillingSubscriptionStatus.Trialing,
        "active" => BillingSubscriptionStatus.Active,
        "past_due" => BillingSubscriptionStatus.PastDue,
        "canceled" => BillingSubscriptionStatus.Canceled,
        "unpaid" => BillingSubscriptionStatus.Unpaid,
        "paused" => BillingSubscriptionStatus.Paused,
        _ => throw new InvalidOperationException($"Unsupported Stripe subscription status '{value}'."),
    };

    private static BillingOverview ToOverview(
        OrganizationBillingAccount account,
        string organizationName,
        int reservedCampaignUsage,
        DateTimeOffset now) => new(
        account.OrganizationId,
        organizationName,
        account.StripeCustomerId,
        account.StripeSubscriptionId,
        account.Plan,
        account.SubscriptionStatus,
        account.CurrentPeriodStartUtc,
        account.CurrentPeriodEndUtc,
        account.MonthlyCampaignAllowance,
        account.CampaignUsage,
        account.AdditionalCampaignUsage,
        reservedCampaignUsage,
        account.Plan == SubscriptionPlan.Free
            && account.MonthlyCampaignAllowance > 0
            && now < account.CurrentPeriodEndUtc);

    private static void EnsureAbsoluteUrl(string value, string parameterName)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("An absolute HTTP or HTTPS URL is required.", parameterName);
        }
    }

    private sealed record BillingIdentity(Guid OrganizationId, string OrganizationName, string OwnerEmail);
}
