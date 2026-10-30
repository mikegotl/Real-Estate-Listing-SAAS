namespace ListingStudio.Infrastructure.Billing;

using System.Data;
using ListingStudio.Application.Administration;
using ListingStudio.Application.Billing;
using ListingStudio.Domain.Billing;
using ListingStudio.Domain.Administration;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Notifications;
using ListingStudio.Infrastructure.Configuration;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

public sealed class BillingService(
    ApplicationDbContext dbContext,
    IBillingProviderGateway provider,
    IOptions<StripeOptions> options,
    TimeProvider timeProvider) : IBillingService, ICampaignBillingGate, IPlanFeatureGate
{
    public async Task<BillingOverview> GetOverviewAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var identity = await GetIdentityAsync(userId, cancellationToken);
        var account = await GetOrCreateAccountAsync(identity.OrganizationId, cancellationToken);
        account.ExpireFreeTrial(timeProvider.GetUtcNow());
        account.AdvanceUsageWindow(timeProvider.GetUtcNow());
        var reservedCampaignUsage = await ReservedCampaignUsageAsync(account, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        var isTester = await IsTesterAsync(userId, cancellationToken);
        var isPlatformAdmin = await IsPlatformAdminAsync(userId, cancellationToken);
        return ToOverview(account, identity.OrganizationName, reservedCampaignUsage, timeProvider.GetUtcNow()) with
        {
            IsTester = isTester,
            IsPlatformAdmin = isPlatformAdmin,
            IncludesPremiumAi = isTester || isPlatformAdmin || IncludesPremiumAi(account, timeProvider.GetUtcNow()),
            TesterMonthlyCampaignLimit = isTester ? await TesterLimitAsync(userId, cancellationToken) : 0,
            TesterCampaignsThisMonth = isTester ? await TesterCampaignsThisMonthAsync(userId, cancellationToken) : 0,
        };
    }

    public async Task<PlanFeatureAccess> GetAccessAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var identity = await GetIdentityAsync(userId, cancellationToken);
        return await GetAccessAsync(identity.OrganizationId, userId, cancellationToken);
    }

    public async Task<PlanFeatureAccess> GetAccessAsync(
        Guid organizationId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var account = await dbContext.OrganizationBillingAccounts.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.OrganizationId == organizationId, cancellationToken);
        var plan = account?.Plan ?? SubscriptionPlan.Trial;
        // Testers and platform admins get every feature.
        var included = await IsFreeUserAsync(userId, cancellationToken)
            || (account is not null && IncludesPremiumAi(account, timeProvider.GetUtcNow()));
        return new PlanFeatureAccess(plan, included);
    }

    public async Task<BillingRedirect> CreateCheckoutSessionAsync(
        string userId,
        SubscriptionPlan plan,
        BillingInterval interval,
        string successUrl,
        string cancelUrl,
        CancellationToken cancellationToken = default)
    {
        if (!SubscriptionPlanFeatures.IsPaid(plan))
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
                interval,
                PriceId(plan, interval),
                successUrl,
                cancelUrl,
                $"checkout:{identity.OrganizationId:D}:{plan}:{interval}"),
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
        // Platform admins are unlimited. Testers need no plan or workspace allowance but have their own monthly limit.
        if (await IsPlatformAdminAsync(requestedByUserId, cancellationToken))
        {
            return;
        }

        if (await IsTesterAsync(requestedByUserId, cancellationToken))
        {
            await EnsureTesterCapacityAsync(organizationId, propertyId, requestedByUserId, cancellationToken);
            return;
        }

        var now = timeProvider.GetUtcNow();
        var account = await GetLockedAccountAsync(organizationId, cancellationToken);
        account.ExpireFreeTrial(now);
        account.AdvanceUsageWindow(now);

        var isTrial = account.Plan == SubscriptionPlan.Trial;
        var activeSubscription = HasActiveSubscription(account, now);
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
                : $"Your {SubscriptionPlanFeatures.DisplayName(account.Plan)} plan's {account.MonthlyCampaignAllowance} campaigns for this month are used or reserved. Upgrade on Billing to continue.");
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
        if (await IsFreeUserAsync(job.RequestedByUserId, cancellationToken))
        {
            return;
        }

        var account = await GetLockedAccountAsync(organizationId, cancellationToken);
        account.AdvanceUsageWindow(timeProvider.GetUtcNow());
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
            ?? throw new InvalidOperationException("The Stripe customer is not linked to a Creative Listing Studio organization.");
        var (plan, interval) = Plan(subscription.PriceId);
        account.ApplySubscription(
            subscription.StripeCustomerId,
            subscription.StripeSubscriptionId,
            plan,
            Status(subscription.Status),
            subscription.CurrentPeriodStartUtc,
            subscription.CurrentPeriodEndUtc,
            Allowance(plan),
            subscription.EventCreatedUtc,
            timeProvider.GetUtcNow(),
            interval);
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
            && !FreeUserIds().Contains(job.RequestedByUserId)
            && !dbContext.CampaignUsageRecords.Any(record =>
                record.CampaignGenerationJobId == job.Id)
            && !dbContext.CampaignUsageRecords.Any(record =>
                record.OrganizationId == account.OrganizationId
                && record.PeriodStartUtc == account.CurrentPeriodStartUtc
                && record.CampaignGenerationJob.PropertyId == job.PropertyId),
            cancellationToken);

    private IQueryable<string> UserIdsInRole(string roleName) =>
        from userRole in dbContext.UserRoles
        join role in dbContext.Roles on userRole.RoleId equals role.Id
        where role.Name == roleName
        select userRole.UserId;

    // Campaigns by testers and platform admins never use a workspace's allowance.
    private IQueryable<string> FreeUserIds() =>
        from userRole in dbContext.UserRoles
        join role in dbContext.Roles on userRole.RoleId equals role.Id
        where role.Name == PlatformTesters.Role || role.Name == PlatformAdministration.Role
        select userRole.UserId;

    private Task<bool> IsTesterAsync(string userId, CancellationToken cancellationToken) =>
        UserIdsInRole(PlatformTesters.Role).AnyAsync(testerId => testerId == userId, cancellationToken);

    private Task<bool> IsPlatformAdminAsync(string userId, CancellationToken cancellationToken) =>
        UserIdsInRole(PlatformAdministration.Role).AnyAsync(adminId => adminId == userId, cancellationToken);

    private Task<bool> IsFreeUserAsync(string userId, CancellationToken cancellationToken) =>
        FreeUserIds().AnyAsync(freeUserId => freeUserId == userId, cancellationToken);

    private async Task<int> TesterLimitAsync(string userId, CancellationToken cancellationToken) =>
        await dbContext.Users.AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => user.TesterMonthlyCampaignLimit)
            .SingleOrDefaultAsync(cancellationToken)
        ?? PlatformTesters.DefaultMonthlyCampaignLimit;

    private Task<int> TesterCampaignsThisMonthAsync(string userId, CancellationToken cancellationToken) =>
        TesterCampaignUsage.CountedJobs(dbContext, [userId], timeProvider.GetUtcNow())
            .Select(job => job.PropertyId)
            .Distinct()
            .CountAsync(cancellationToken);

    private async Task EnsureTesterCapacityAsync(
        Guid organizationId,
        Guid propertyId,
        string testerUserId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var counted = await TesterCampaignUsage.CountedJobs(dbContext, [testerUserId], now)
            .Select(job => job.PropertyId)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (counted.Contains(propertyId))
        {
            return;
        }

        var limit = await TesterLimitAsync(testerUserId, cancellationToken);
        if (counted.Count >= limit)
        {
            throw new CampaignAllowanceExceededException(
                $"You've used all {limit} tester campaigns for {now.UtcDateTime:MMMM}. Ask the Creative Listing Studio team to raise your limit, or wait until next month.");
        }

        if (counted.Count + 1 == limit)
        {
            await NotifyAdminsOfLastTesterCampaignAsync(organizationId, propertyId, testerUserId, limit, now, cancellationToken);
        }
    }

    // Added to the context and saved with the campaign that uses the last slot, so a failed request sends nothing.
    private async Task NotifyAdminsOfLastTesterCampaignAsync(
        Guid organizationId,
        Guid propertyId,
        string testerUserId,
        int limit,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var monthStart = PlatformTesters.MonthStart(now);
        if (await dbContext.UserNotifications.AnyAsync(notification =>
                notification.Kind == UserNotificationKind.TesterCampaignLimit
                && notification.OrganizationId == organizationId
                && notification.PropertyId == propertyId
                && notification.CreatedAtUtc >= monthStart,
                cancellationToken))
        {
            return;
        }

        var tester = await dbContext.Users.AsNoTracking()
            .Where(user => user.Id == testerUserId)
            .Select(user => user.Email ?? user.UserName ?? user.Id)
            .SingleAsync(cancellationToken);
        var admins = await dbContext.Users.AsNoTracking()
            .Where(user => UserIdsInRole(PlatformAdministration.Role).Contains(user.Id))
            .Select(user => new { user.Id, user.Email })
            .ToListAsync(cancellationToken);
        var month = now.UtcDateTime.ToString("MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
        var title = $"Tester on last campaign: {tester}";
        var message = $"{tester} just started campaign {limit} of {limit} for {month}. "
            + "Raise their limit on Find users > Testers if they need more.";
        var body = string.Join('\n',
            "Hi,",
            string.Empty,
            $"{tester} just started their last tester campaign for {month} ({limit} of {limit}). After this one they can't start a campaign for another property until next month.",
            string.Empty,
            "To give them more, open Admin > Find users > Testers and raise their monthly campaign limit.",
            string.Empty,
            "Creative Listing Studio");
        foreach (var admin in admins)
        {
            dbContext.UserNotifications.Add(UserNotification.Create(
                organizationId,
                admin.Id,
                propertyId,
                UserNotificationKind.TesterCampaignLimit,
                UserNotificationOutcome.Succeeded,
                Truncate(title, UserNotification.MaximumTitleLength),
                Truncate(message, UserNotification.MaximumMessageLength),
                "admin-testers",
                now));
            var email = admin.Email?.Trim();
            if (!string.IsNullOrEmpty(email)
                && email.Length <= AdminUserEmail.MaximumEmailLength
                && MimeKit.MailboxAddress.TryParse(email, out _))
            {
                // The admin email outbox sends it once SMTP is configured, and it shows on Sent emails.
                dbContext.AdminUserEmails.Add(AdminUserEmail.Create(
                    Guid.NewGuid(),
                    admin.Id,
                    admin.Id,
                    email,
                    Truncate(title, AdminUserEmail.MaximumSubjectLength),
                    body,
                    now));
            }
        }
    }

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..(maximumLength - 1)] + "…";

    private async Task<bool> PropertyCampaignUsageExistsAsync(
        OrganizationBillingAccount account,
        Guid propertyId,
        CancellationToken cancellationToken) =>
        await dbContext.CampaignUsageRecords.AsNoTracking().AnyAsync(record =>
            record.OrganizationId == account.OrganizationId
            && record.PeriodStartUtc == account.CurrentPeriodStartUtc
            && record.CampaignGenerationJob.PropertyId == propertyId,
            cancellationToken);

    private static bool HasActiveSubscription(OrganizationBillingAccount account, DateTimeOffset now) =>
        SubscriptionPlanFeatures.IsPaid(account.Plan)
        && account.SubscriptionStatus is BillingSubscriptionStatus.Active or BillingSubscriptionStatus.Trialing
        && now < account.PaidThroughUtc;

    private static bool IncludesPremiumAi(OrganizationBillingAccount account, DateTimeOffset now) =>
        SubscriptionPlanFeatures.IncludesPremiumAi(account.Plan) && HasActiveSubscription(account, now);

    private string PriceId(SubscriptionPlan plan, BillingInterval interval)
    {
        var priceId = PriceIds().SingleOrDefault(price => price.Plan == plan && price.Interval == interval).PriceId;
        return !string.IsNullOrWhiteSpace(priceId)
            ? priceId
            : throw new InvalidOperationException(
                $"Stripe price configuration is missing for {interval.ToString().ToLowerInvariant()} {plan} billing.");
    }

    private (SubscriptionPlan Plan, BillingInterval Interval) Plan(string priceId)
    {
        foreach (var price in PriceIds())
        {
            if (!string.IsNullOrWhiteSpace(price.PriceId) && string.Equals(priceId, price.PriceId, StringComparison.Ordinal))
            {
                return (price.Plan, price.Interval);
            }
        }

        throw new InvalidOperationException("The Stripe subscription uses an unknown price.");
    }

    private (SubscriptionPlan Plan, BillingInterval Interval, string PriceId)[] PriceIds() =>
    [
        (SubscriptionPlan.Basic, BillingInterval.Monthly, options.Value.BasicPriceId),
        (SubscriptionPlan.Professional, BillingInterval.Monthly, options.Value.ProfessionalPriceId),
        (SubscriptionPlan.Enterprise, BillingInterval.Monthly, options.Value.EnterprisePriceId),
        (SubscriptionPlan.Basic, BillingInterval.Annual, options.Value.BasicAnnualPriceId),
        (SubscriptionPlan.Professional, BillingInterval.Annual, options.Value.ProfessionalAnnualPriceId),
        (SubscriptionPlan.Enterprise, BillingInterval.Annual, options.Value.EnterpriseAnnualPriceId),
    ];

    private int Allowance(SubscriptionPlan plan) => plan switch
    {
        SubscriptionPlan.Basic => options.Value.BasicMonthlyCampaignAllowance,
        SubscriptionPlan.Professional => options.Value.ProfessionalMonthlyCampaignAllowance,
        SubscriptionPlan.Enterprise => options.Value.EnterpriseMonthlyCampaignAllowance,
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
        account.Plan == SubscriptionPlan.Trial
            && account.MonthlyCampaignAllowance > 0
            && now < account.CurrentPeriodEndUtc,
        BillingInterval: account.BillingInterval,
        PaidThroughUtc: account.SubscriptionPeriodEndUtc);

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
