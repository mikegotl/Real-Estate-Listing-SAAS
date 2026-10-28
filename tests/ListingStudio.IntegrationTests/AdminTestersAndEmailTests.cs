using System.Net;
using System.Text.RegularExpressions;
using ListingStudio.Application.Administration;
using ListingStudio.Application.Authentication;
using ListingStudio.Application.Billing;
using ListingStudio.Application.Email;
using ListingStudio.Application.Properties;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Properties;
using ListingStudio.Infrastructure.Administration;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed class AdminTestersAndEmailTests(PostgreSqlWebApplicationFixture fixture)
    : IClassFixture<PostgreSqlWebApplicationFixture>
{
    private const string Password = "Password123";

    [Fact]
    public async Task AdminCanAddAndRemoveATesterFromFindUsersAndEachChangeIsAudited()
    {
        var marker = Guid.NewGuid().ToString("N")[..10];
        var admin = await RegisterAsync($"tester-admin-{marker}@example.com", $"Admin {marker}");
        await GrantPlatformAdminAsync(admin.Email);
        var customer = await RegisterAsync($"future-tester-{marker}@example.com", $"Tester {marker} Realty");
        using var client = CookieClient();
        await LogInAsync(client, admin.Email);

        using var search = await client.GetAsync($"/admin/users?q={marker}");
        search.EnsureSuccessStatusCode();
        var searchHtml = await search.Content.ReadAsStringAsync();
        Assert.Contains("Add to Testers", searchHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Tester · free", searchHtml, StringComparison.Ordinal);

        using var add = await PostTesterAsync(client, searchHtml, customer.UserId, isTester: true, marker);
        Assert.Equal(HttpStatusCode.Redirect, add.StatusCode);
        Assert.Equal($"/admin/users?q={marker}&notice=tester-added-emailed", add.Headers.Location?.OriginalString);

        using var testers = await client.GetAsync("/admin/users?testers=true");
        testers.EnsureSuccessStatusCode();
        var testersHtml = await testers.Content.ReadAsStringAsync();
        Assert.Contains(customer.Email, testersHtml, StringComparison.Ordinal);
        Assert.Contains("Tester · free", testersHtml, StringComparison.Ordinal);
        Assert.Contains("Remove from Testers", testersHtml, StringComparison.Ordinal);

        using var remove = await PostTesterAsync(client, testersHtml, customer.UserId, isTester: false, marker, testersView: true);
        Assert.Equal("/admin/users?testers=true&notice=tester-removed", remove.Headers.Location?.OriginalString);
        using var removeAgain = await PostTesterAsync(client, testersHtml, customer.UserId, isTester: false, marker);
        Assert.Equal($"/admin/users?q={marker}&notice=tester-unchanged", removeAgain.Headers.Location?.OriginalString);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var directory = scope.ServiceProvider.GetRequiredService<IPlatformAdminAccountDirectory>();
        Assert.DoesNotContain((await directory.ListTestersAsync()).Accounts, account => account.UserId == customer.UserId);
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var events = await dbContext.PlatformAdminAuditEvents.AsNoTracking()
            .Where(item => item.TargetReference == AuditPseudonym.For(customer.UserId)
                && item.Action != AdminUserEmailAudit.QueueAction)
            .Select(item => new { item.Action, item.Outcome, item.ActorReference })
            .ToListAsync();
        Assert.Equal(
            new[]
            {
                (PlatformTesters.AddAction, "added"),
                (PlatformTesters.RemoveAction, "removed"),
                (PlatformTesters.RemoveAction, "unchanged"),
            }.Order(),
            events.Select(item => (item.Action, item.Outcome)).Order());
        Assert.All(events, item => Assert.Equal(AuditPseudonym.For(admin.UserId), item.ActorReference));

        // Adding sends one welcome email; removing and re-removing send nothing.
        var welcome = Assert.Single(await dbContext.AdminUserEmails.AsNoTracking()
            .Where(item => item.RecipientUserId == customer.UserId)
            .ToListAsync());
        Assert.Equal(PlatformTesters.WelcomeEmailSubject, welcome.Subject);
        Assert.Equal(customer.Email, welcome.RecipientEmail);
        Assert.Equal(admin.UserId, welcome.SentByUserId);
        Assert.Null(welcome.SentAtUtc);
    }

    [Fact]
    public async Task OnlyPlatformAdminsCanChangeTestersAndDeniedAttemptsAreAudited()
    {
        var marker = Guid.NewGuid().ToString("N")[..10];
        var customer = await RegisterAsync($"not-admin-{marker}@example.com", $"Plain {marker}");
        var target = await RegisterAsync($"target-{marker}@example.com", $"Target {marker}");

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var access = scope.ServiceProvider.GetRequiredService<IPlatformAdminTesterAccess>();
            var result = await access.SetTesterAsync(customer.UserId, target.UserId, isTester: true);
            Assert.Equal(TesterChangeFailure.NotPlatformAdmin, result.Failure);
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.True(await dbContext.PlatformAdminAuditEvents.AnyAsync(item =>
                item.Action == PlatformTesters.AddAction
                && item.Outcome == "denied"
                && item.TargetReference == AuditPseudonym.For(target.UserId)));
            Assert.DoesNotContain(
                (await scope.ServiceProvider.GetRequiredService<IPlatformAdminAccountDirectory>().ListTestersAsync()).Accounts,
                account => account.UserId == target.UserId);
        }

        using var client = CookieClient();
        await LogInAsync(client, customer.Email);
        using var page = await client.GetAsync("/admin/users?testers=true");
        Assert.NotEqual(HttpStatusCode.OK, page.StatusCode);
        using var email = await client.GetAsync($"/admin/email?to={target.UserId}");
        Assert.NotEqual(HttpStatusCode.OK, email.StatusCode);
    }

    [Fact]
    public async Task TestersCreateCampaignsWithoutAnAllowanceAndTheyAreNotCounted()
    {
        var marker = Guid.NewGuid().ToString("N")[..10];
        var admin = await RegisterAsync($"billing-admin-{marker}@example.com", $"Billing Admin {marker}");
        await GrantPlatformAdminAsync(admin.Email);
        var customer = await RegisterAsync($"billing-tester-{marker}@example.com", $"Billing Tester {marker}");
        var propertyId = await CreatePropertyAsync(customer.UserId, $"{marker} Free Street");

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var dbContext = services.GetRequiredService<ApplicationDbContext>();
        var billing = services.GetRequiredService<IBillingService>();
        var gate = services.GetRequiredService<ICampaignBillingGate>();
        var overview = await billing.GetOverviewAsync(customer.UserId);
        Assert.False(overview.IsTester);
        var organizationId = overview.OrganizationId;
        // An ended trial: an ordinary customer cannot start a campaign.
        await dbContext.OrganizationBillingAccounts
            .Where(account => account.OrganizationId == organizationId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(account => account.MonthlyCampaignAllowance, 0));
        dbContext.ChangeTracker.Clear();
        await Assert.ThrowsAsync<CampaignAllowanceExceededException>(() =>
            gate.EnsureCampaignCapacityAsync(organizationId, propertyId, customer.UserId));

        var added = await services.GetRequiredService<IPlatformAdminTesterAccess>()
            .SetTesterAsync(admin.UserId, customer.UserId, isTester: true);
        Assert.True(added.Succeeded);
        Assert.True(added.Changed);

        await gate.EnsureCampaignCapacityAsync(organizationId, propertyId, customer.UserId);
        Assert.True((await billing.GetOverviewAsync(customer.UserId)).IsTester);

        var job = CampaignGenerationJob.Create(
            organizationId,
            propertyId,
            customer.UserId,
            new string('c', 64),
            CampaignOutputSelection.LegacyStandard,
            fixture.Factory.TimeProvider.GetUtcNow());
        dbContext.CampaignGenerationJobs.Add(job);
        await dbContext.SaveChangesAsync();
        await gate.RecordCompletedCampaignAsync(organizationId, job.Id);
        await dbContext.SaveChangesAsync();

        Assert.False(await dbContext.CampaignUsageRecords.AnyAsync(record => record.CampaignGenerationJobId == job.Id));
        Assert.Equal(0, (await billing.GetOverviewAsync(customer.UserId)).CampaignUsage);

        Assert.True((await services.GetRequiredService<IPlatformAdminTesterAccess>()
            .SetTesterAsync(admin.UserId, customer.UserId, isTester: false)).Changed);
        await Assert.ThrowsAsync<CampaignAllowanceExceededException>(() =>
            gate.EnsureCampaignCapacityAsync(organizationId, propertyId, customer.UserId));
    }

    [Fact]
    public async Task AdminEmailsAreQueuedPerRecipientAuditedSentByTheWorkerAndLogged()
    {
        var marker = Guid.NewGuid().ToString("N")[..10];
        var admin = await RegisterAsync($"email-admin-{marker}@example.com", $"Email Admin {marker}");
        await GrantPlatformAdminAsync(admin.Email);
        var first = await RegisterAsync($"email-first-{marker}@example.com", $"First {marker}");
        var second = await RegisterAsync($"email-second-{marker}@example.com", $"Second {marker}");

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var dbContext = services.GetRequiredService<ApplicationDbContext>();
        await dbContext.AdminUserEmails.ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.SentAtUtc, fixture.Factory.TimeProvider.GetUtcNow()));
        var emails = services.GetRequiredService<IAdminUserEmailService>();

        Assert.Equal(
            AdminUserEmailFailure.NotPlatformAdmin,
            (await emails.QueueAsync(first.UserId, new AdminUserEmailRequest([second.UserId], "Hi", "Body"))).Failure);
        Assert.Equal(
            AdminUserEmailFailure.SubjectRequired,
            (await emails.QueueAsync(admin.UserId, new AdminUserEmailRequest([first.UserId], "  ", "Body"))).Failure);
        Assert.Equal(
            AdminUserEmailFailure.NoRecipients,
            (await emails.QueueAsync(admin.UserId, new AdminUserEmailRequest([], "Hi", "Body"))).Failure);

        var queued = await emails.QueueAsync(
            admin.UserId,
            new AdminUserEmailRequest(
                [first.UserId, second.UserId, first.UserId, "missing-user"],
                $"Welcome {marker}\r\nto testing",
                "Thanks for helping us test Listing Studio."));
        Assert.True(queued.Succeeded);
        Assert.Equal(2, queued.QueuedCount);
        Assert.Equal(1, queued.SkippedCount);

        var log = (await emails.GetLogAsync()).Where(row => row.Subject == $"Welcome {marker} to testing").ToList();
        Assert.Equal(2, log.Count);
        Assert.All(log, row => Assert.Equal(AdminUserEmailStatus.Queued, row.Status));
        Assert.All(log, row => Assert.Equal(admin.Email, row.SentByEmail));
        Assert.Single(log.Select(row => row.BatchId).Distinct());

        var sender = new RecordingEmailSender { FailNext = true };
        var processor = new AdminUserEmailProcessor(dbContext, sender, fixture.Factory.TimeProvider);
        var failed = await processor.ProcessNextAsync();
        Assert.Equal(AdminUserEmailSendOutcome.Failed, failed?.Outcome);
        Assert.True(failed!.WillRetry);
        Assert.Equal(AdminUserEmailSendOutcome.Sent, (await processor.ProcessNextAsync())?.Outcome);
        // The failed email waits for its back-off before it is tried again.
        Assert.Null(await processor.ProcessNextAsync());
        fixture.Factory.TimeProvider.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(AdminUserEmailSendOutcome.Sent, (await processor.ProcessNextAsync())?.Outcome);
        Assert.Null(await processor.ProcessNextAsync());

        Assert.Equal(2, sender.Sent.Count);
        Assert.All(sender.Sent, message =>
        {
            Assert.Empty(message.BlindCopyRecipients);
            Assert.Equal(admin.Email, message.ReplyTo);
            Assert.Contains("Thanks for helping us test Listing Studio.", message.TextBody, StringComparison.Ordinal);
            Assert.Contains("Sent by the Listing Studio team", message.TextBody, StringComparison.Ordinal);
        });
        Assert.Equal(
            [first.Email, second.Email],
            sender.Sent.SelectMany(message => message.ToRecipients!).Order(StringComparer.Ordinal));

        dbContext.ChangeTracker.Clear();
        var sentLog = (await emails.GetLogAsync()).Where(row => row.Subject == $"Welcome {marker} to testing").ToList();
        Assert.All(sentLog, row => Assert.Equal(AdminUserEmailStatus.Sent, row.Status));
        Assert.Contains(sentLog, row => row.AttemptCount == 2);
        var audits = await dbContext.PlatformAdminAuditEvents.AsNoTracking()
            .Where(item => item.Action == AdminUserEmailAudit.QueueAction && item.ActorReference == AuditPseudonym.For(admin.UserId))
            .Select(item => item.TargetReference)
            .ToListAsync();
        Assert.Equal(
            new[] { AuditPseudonym.For(first.UserId), AuditPseudonym.For(second.UserId) }.Order(),
            audits.Order());
    }

    [Fact]
    public async Task AdminCanEmailSelectedUsersFromTheComposePage()
    {
        var marker = Guid.NewGuid().ToString("N")[..10];
        var admin = await RegisterAsync($"compose-admin-{marker}@example.com", $"Compose Admin {marker}");
        await GrantPlatformAdminAsync(admin.Email);
        var customer = await RegisterAsync($"compose-user-{marker}@example.com", $"Compose {marker}");
        using var client = CookieClient();
        await LogInAsync(client, admin.Email);

        using var search = await client.GetAsync($"/admin/users?q={marker}");
        var searchHtml = await search.Content.ReadAsStringAsync();
        Assert.Contains("Email selected", searchHtml, StringComparison.Ordinal);
        Assert.Contains($"/admin/email?to={customer.UserId}", searchHtml, StringComparison.Ordinal);

        var composeUrl = $"/admin/email?to={customer.UserId}&q={marker}";
        using var compose = await client.GetAsync(composeUrl);
        compose.EnsureSuccessStatusCode();
        var composeHtml = await compose.Content.ReadAsStringAsync();
        Assert.Contains(customer.Email, composeHtml, StringComparison.Ordinal);
        Assert.Contains("Send to 1 user", composeHtml, StringComparison.Ordinal);

        using var send = await client.PostAsync(composeUrl, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "admin-email",
            ["__RequestVerificationToken"] = HiddenValue(composeHtml, "__RequestVerificationToken"),
            ["Input.Subject"] = $"Compose {marker}",
            ["Input.Body"] = "Your tester access is ready.",
        }));
        Assert.Equal(HttpStatusCode.Redirect, send.StatusCode);
        Assert.Equal($"/admin/email?sent=1&skipped=0&q={marker}", send.Headers.Location?.PathAndQuery);

        using var logPage = await client.GetAsync(send.Headers.Location!.PathAndQuery);
        var logHtml = await logPage.Content.ReadAsStringAsync();
        Assert.Contains("1 email queued.", logHtml, StringComparison.Ordinal);
        Assert.Contains($"Compose {marker}", logHtml, StringComparison.Ordinal);
        Assert.Contains("Queued", logHtml, StringComparison.Ordinal);
    }

    private static async Task<HttpResponseMessage> PostTesterAsync(
        HttpClient client,
        string html,
        string userId,
        bool isTester,
        string query,
        bool testersView = false) =>
        await client.PostAsync("/admin/users/testers", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = HiddenValue(html, "__RequestVerificationToken"),
            ["userId"] = userId,
            ["isTester"] = isTester ? "true" : "false",
            ["q"] = query,
            ["testers"] = testersView ? "true" : "false",
        }));

    private HttpClient CookieClient() => fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
    });

    private static async Task LogInAsync(HttpClient client, string email)
    {
        using var form = await client.GetAsync("/Account/Login");
        form.EnsureSuccessStatusCode();
        var html = await form.Content.ReadAsStringAsync();
        using var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "login",
            ["__RequestVerificationToken"] = HiddenValue(html, "__RequestVerificationToken"),
            ["Input.Email"] = email,
            ["Input.Password"] = Password,
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private async Task<(string UserId, string Email)> RegisterAsync(string email, string workspace)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var registration = scope.ServiceProvider.GetRequiredService<IAccountRegistrationService>();
        var result = await registration.RegisterAsync(new RegisterAccountCommand(email, Password, workspace));
        Assert.True(result.Succeeded);
        return (result.UserId!, email);
    }

    private async Task GrantPlatformAdminAsync(string email)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PlatformAdminRoleBootstrapper>().GrantAsync(email);
    }

    private async Task<Guid> CreatePropertyAsync(string userId, string address)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var properties = scope.ServiceProvider.GetRequiredService<IPropertyService>();
        return await properties.CreateAsync(userId, new PropertyInput(
            address, null, "Raleigh", "NC", "27601", 450_000m, 3, 2m, 1_700, 0.2m, 2001,
            PropertyType.SingleFamily, "Tester billing property.", ListingStatus.Draft));
    }

    private static string HiddenValue(string html, string name)
    {
        var match = Regex.Match(html, $"name=\"{Regex.Escape(name)}\"[^>]*value=\"([^\"]*)\"");
        Assert.True(match.Success, $"The page has no {name} field.");
        return System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private sealed class RecordingEmailSender : IOutboundEmailSender
    {
        public List<OutboundEmail> Sent { get; } = [];

        public bool FailNext { get; set; }

        public bool IsConfigured => true;

        public Task SendAsync(OutboundEmail email, CancellationToken cancellationToken = default)
        {
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("SMTP server unavailable.");
            }

            Sent.Add(email);
            return Task.CompletedTask;
        }
    }
}
