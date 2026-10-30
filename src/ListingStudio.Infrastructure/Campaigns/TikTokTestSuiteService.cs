using ListingStudio.Application.Administration;
using ListingStudio.Application.Campaigns;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Infrastructure.Identity;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ListingStudio.Domain.Videos;

namespace ListingStudio.Infrastructure.Campaigns;

public sealed class TikTokTestSuiteService(ApplicationDbContext dbContext, UserManager<ApplicationUser> users,
    ICampaignTestGenerationService campaigns, TimeProvider timeProvider) : ITikTokTestSuiteService, ITikTokTestSuiteProcessor
{
    // A session lock remains held across CampaignGenerationService's own transaction. All suite mutations use
    // the same lock; a process/connection failure releases it. A deterministic case key recovers enqueue crashes.
    private const long LockKey = 703_600_301_501;

    public async Task<IReadOnlyList<TikTokTestPropertyChoice>> PropertyChoicesAsync(string userId, CancellationToken cancellationToken = default)
    {
        var org = await AuthorizeAsync(userId, cancellationToken);
        return await dbContext.Properties.AsNoTracking().Where(p => p.OrganizationId == org && p.ArchivedAtUtc == null)
            .OrderBy(p => p.Address1).Take(200)
            .Select(p => new TikTokTestPropertyChoice(p.Id, p.Address1 + ", " + p.City + ", " + p.State))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TikTokTestSuiteResult>> ListAsync(string userId, CancellationToken cancellationToken = default)
    {
        var org = await AuthorizeAsync(userId, cancellationToken);
        var suites = await dbContext.TikTokTestSuites.AsNoTracking().Where(s => s.OrganizationId == org)
            .OrderByDescending(s => s.CreatedAtUtc).Take(30).ToListAsync(cancellationToken);
        var ids = suites.Select(s => s.PropertyId).ToArray();
        var properties = await dbContext.Properties.AsNoTracking().Where(p => p.OrganizationId == org && ids.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Address1 + ", " + p.City + ", " + p.State, cancellationToken);
        return suites.Select(s => ToResult(s, properties.GetValueOrDefault(s.PropertyId, "Unavailable property"))).ToArray();
    }

    public async Task<TikTokTestSuiteResult> StartAsync(string userId, Guid propertyId, CancellationToken cancellationToken = default)
    {
        var org = await AuthorizeAsync(userId, cancellationToken);
        if (!await LockAsync(cancellationToken)) throw new InvalidOperationException("A test worker is updating the queue. Try again shortly.");
        try
        {
            var property = await dbContext.Properties.AsNoTracking()
                .SingleOrDefaultAsync(p => p.Id == propertyId && p.OrganizationId == org && p.ArchivedAtUtc == null, cancellationToken)
                ?? throw new KeyNotFoundException("Choose an available property in your workspace.");
            if (await dbContext.TikTokTestSuites.AnyAsync(s => s.OrganizationId == org
                && (s.Status == TikTokTestSuiteStatus.Queued || s.Status == TikTokTestSuiteStatus.Running), cancellationToken))
                throw new InvalidOperationException("A test suite is already active in this workspace.");
            var suite = TikTokTestSuite.Create(org, propertyId, userId, timeProvider.GetUtcNow());
            dbContext.TikTokTestSuites.Add(suite);
            await dbContext.SaveChangesAsync(cancellationToken);
            return ToResult(suite, $"{property.Address1}, {property.City}, {property.State}");
        }
        finally { await UnlockAsync(); }
    }

    public async Task CancelAsync(string userId, Guid suiteId, CancellationToken cancellationToken = default)
    {
        var org = await AuthorizeAsync(userId, cancellationToken);
        if (!await LockAsync(cancellationToken)) throw new InvalidOperationException("A test worker is updating the queue. Try again shortly.");
        try
        {
            var suite = await dbContext.TikTokTestSuites.SingleOrDefaultAsync(s => s.Id == suiteId && s.OrganizationId == org, cancellationToken)
                ?? throw new KeyNotFoundException("This test suite is unavailable in your workspace.");
            suite.Stop(true, "Remaining cases cancelled. Any campaign already running continues separately.", timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        finally { await UnlockAsync(); }
    }

    public async Task ProcessNextAsync(CancellationToken cancellationToken = default)
    {
        if (!await LockAsync(cancellationToken)) return;
        try
        {
            // Rotate waiting suites so a busy property cannot starve another workspace.
            var suite = await dbContext.TikTokTestSuites
                .Where(s => s.Status == TikTokTestSuiteStatus.Queued || s.Status == TikTokTestSuiteStatus.Running)
                .OrderBy(s => s.UpdatedAtUtc).ThenBy(s => s.Id).FirstOrDefaultAsync(cancellationToken);
            if (suite is null) return;
            try
            {
                var org = await AuthorizeAsync(suite.RequestedByUserId, cancellationToken);
                if (org != suite.OrganizationId) throw new UnauthorizedAccessException("The test owner's workspace changed.");
                if (suite.CurrentJobId is { } jobId)
                {
                    var job = await dbContext.CampaignGenerationJobs.AsNoTracking().SingleOrDefaultAsync(j =>
                        j.Id == jobId && j.OrganizationId == org && j.PropertyId == suite.PropertyId, cancellationToken);
                    if (job is null) suite.Stop(false, "The test campaign is unavailable.", timeProvider.GetUtcNow());
                    else if (job.Status == CampaignGenerationStatus.Completed)
                    {
                        var video = await dbContext.CampaignDeliverables.AsNoTracking().SingleOrDefaultAsync(v =>
                            v.OrganizationId == org && v.CampaignGenerationJobId == jobId && v.Kind == CampaignOutputKind.TikTok, cancellationToken);
                        var expectedDuration = suite.CurrentCaseIndex == 2 ? RequestedDuration.Hero60 : RequestedDuration.Feature30;
                        if (video is null || video.Status != CampaignDeliverableStatus.Rendered
                            || video.AspectRatio != VideoAspectRatio.Vertical9By16 || video.RequestedDuration != expectedDuration
                            || video.FileSize is null or <= 0)
                            suite.Stop(false, "The completed campaign has no valid rendered vertical deliverable with the expected duration.", timeProvider.GetUtcNow());
                        else suite.CompleteCase(timeProvider.GetUtcNow());
                    }
                    else if (job.Status is CampaignGenerationStatus.Failed or CampaignGenerationStatus.Cancelled)
                        suite.Stop(job.Status == CampaignGenerationStatus.Cancelled, job.LastError ?? "The campaign stopped before completion.", timeProvider.GetUtcNow());
                }
                else
                {
                    if (!await dbContext.Properties.AsNoTracking().AnyAsync(p =>
                        p.Id == suite.PropertyId && p.OrganizationId == org && p.ArchivedAtUtc == null, cancellationToken))
                        throw new KeyNotFoundException("The test property is no longer available.");
                    var options = OptionsFor(suite.CurrentCaseIndex);
                    var keyBytes = suite.Id.ToByteArray();
                    keyBytes[15] ^= (byte)(suite.CurrentCaseIndex + 1);
                    var queued = await campaigns.EnqueueTestAsync(suite.RequestedByUserId, suite.PropertyId,
                        options, new Guid(keyBytes), cancellationToken);
                    if (queued is not null) suite.AttachJob(queued.Id, timeProvider.GetUtcNow());
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or InvalidOperationException or KeyNotFoundException)
            {
                suite.Stop(false, exception.Message, timeProvider.GetUtcNow());
            }
            // Also advances the polling order when another campaign owns this property's active slot.
            if (dbContext.Entry(suite).State == EntityState.Detached)
                dbContext.TikTokTestSuites.Update(suite);
            dbContext.Entry(suite).Property(s => s.UpdatedAtUtc).CurrentValue = timeProvider.GetUtcNow();
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        finally { await UnlockAsync(); }
    }

    public static CampaignCreativeOptions OptionsFor(int index)
    {
        if (index is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(index));
        return CampaignCreativeOptions.SocialDefault with
        {
            Elements = CampaignCreativeOptions.SocialDefault.Elements & ~CampaignVideoElement.WalkthroughMotion,
            Soundtrack = CampaignSoundtrack.ModernAmbient,
            Voiceover = index switch { 0 => CampaignVoiceoverMode.ShortHook, 1 => CampaignVoiceoverMode.None, _ => CampaignVoiceoverMode.Full },
            Pace = index == 2 ? CampaignPace.Cinematic : CampaignPace.Fast,
        };
    }

    private async Task<Guid> AuthorizeAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var user = await users.FindByIdAsync(userId);
        if (user is null || !await users.IsInRoleAsync(user, PlatformAdministration.Role))
            throw new UnauthorizedAccessException("Platform administration access is required to run campaign tests.");
        return await dbContext.OrganizationMembers.AsNoTracking().Where(m => m.UserId == userId)
            .Select(m => (Guid?)m.OrganizationId).FirstOrDefaultAsync(cancellationToken)
            ?? throw new UnauthorizedAccessException("The administrator does not belong to a workspace.");
    }

    private async Task<bool> LockAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.Database.OpenConnectionAsync(cancellationToken);
            await using var command = dbContext.Database.GetDbConnection().CreateCommand();
            command.CommandText = $"SELECT pg_try_advisory_lock({LockKey})";
            if (await command.ExecuteScalarAsync(cancellationToken) is true) return true;
            await dbContext.Database.CloseConnectionAsync();
            return false;
        }
        catch
        {
            // Cancellation can arrive after PostgreSQL acquired the session lock but before the response arrives.
            // Returning/resetting the connection releases that lock even though the caller never entered its finally.
            await dbContext.Database.CloseConnectionAsync();
            throw;
        }
    }

    private async Task UnlockAsync()
    {
        try
        {
            await using var command = dbContext.Database.GetDbConnection().CreateCommand();
            command.CommandText = $"SELECT pg_advisory_unlock({LockKey})";
            await command.ExecuteScalarAsync(CancellationToken.None);
        }
        finally { await dbContext.Database.CloseConnectionAsync(); }
    }

    private static TikTokTestSuiteResult ToResult(TikTokTestSuite s, string address) => new(s.Id, s.PropertyId, address,
        s.Status, s.CurrentCaseIndex, s.Error, [s.ShortVoiceoverJobId, s.MusicOnlyJobId, s.FullVoiceoverJobId], s.CreatedAtUtc);
}
