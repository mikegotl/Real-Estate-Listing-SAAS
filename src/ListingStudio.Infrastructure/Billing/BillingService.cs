namespace ListingStudio.Infrastructure.Billing;

using System.Data;
using ListingStudio.Application.Billing;
using ListingStudio.Domain.Billing;
using ListingStudio.Infrastructure.Configuration;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

public sealed class BillingService(
    ApplicationDbContext dbContext,
    IBillingProviderGateway provider,
    IOptions<StripeOptions> options,
    TimeProvider timeProvider) : IBillingService, IBillingUsageRecorder
{
    public async Task<BillingOverview> GetOverviewAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var identity = await GetIdentityAsync(userId, cancellationToken);
        var account = await GetOrCreateAccountAsync(identity.OrganizationId, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToOverview(account, identity.OrganizationName);
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

    public async Task RecordCampaignAsync(
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

        var account = await dbContext.OrganizationBillingAccounts
            .FromSqlInterpolated($"""
                SELECT * FROM "OrganizationBillingAccounts"
                WHERE "OrganizationId" = {organizationId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);
        if (account is null)
        {
            account = OrganizationBillingAccount.Create(organizationId, timeProvider.GetUtcNow());
            dbContext.OrganizationBillingAccounts.Add(account);
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

        account = OrganizationBillingAccount.Create(organizationId, timeProvider.GetUtcNow());
        dbContext.OrganizationBillingAccounts.Add(account);
        return account;
    }

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

    private static BillingOverview ToOverview(OrganizationBillingAccount account, string organizationName) => new(
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
        account.AdditionalCampaignUsage);

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
