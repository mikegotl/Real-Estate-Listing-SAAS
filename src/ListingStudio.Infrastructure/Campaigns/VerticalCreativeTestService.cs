using System.Text.Json;
using ListingStudio.Application.Administration;
using ListingStudio.Application.Audio;
using ListingStudio.Application.Campaigns;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Administration;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Videos;
using ListingStudio.Infrastructure.Identity;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ListingStudio.Infrastructure.Campaigns;

/// <summary>
/// Creative format tests: platform administrators only, in their current workspace, only while VerticalVideoLibraryV1
/// is on for it. Cases run one at a time under their own advisory lock, so they never wait on or block the render
/// regression suite (<see cref="TikTokTestSuiteService"/>). Each case validates its own deliverable: frame size,
/// length, file health, a full decode and the recipe version it was scored for.
/// </summary>
public sealed partial class VerticalCreativeTestService(
    ApplicationDbContext dbContext,
    UserManager<ApplicationUser> users,
    IFeatureStateService featureStateService,
    IVerticalCreativePlanningService planningService,
    ICampaignTestGenerationService campaigns,
    ICampaignAssetStorage assetStorage,
    IRenderedVideoValidator videoValidator,
    TimeProvider timeProvider,
    ILogger<VerticalCreativeTestService> logger) : IVerticalCreativeTestService, IVerticalCreativeTestProcessor
{
    private const long LockKey = 703_600_301_509;

    public async Task<bool> IsEnabledAsync(string userId, CancellationToken cancellationToken = default)
    {
        try
        {
            await AuthorizeAsync(userId, cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or CreativeTestingDisabledException)
        {
            return false;
        }
    }

    public async Task<VerticalCreativeTestPreview?> PreviewAsync(string userId, Guid propertyId, CancellationToken cancellationToken = default)
    {
        var organizationId = await AuthorizeAsync(userId, cancellationToken);
        return await PreviewCoreAsync(organizationId, propertyId, cancellationToken);
    }

    public async Task<VerticalCreativeTestSuiteView> StartAsync(string userId, Guid propertyId, VerticalCreativeTestMode mode,
        VerticalCreativeFormat? format, CampaignCaptionStyle captionStyle = CampaignCaptionStyles.Default,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await AuthorizeAsync(userId, cancellationToken);
        if (!Enum.IsDefined(captionStyle))
        {
            throw new ArgumentOutOfRangeException(nameof(captionStyle));
        }

        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        var preview = await PreviewCoreAsync(organizationId, propertyId, cancellationToken)
            ?? throw new KeyNotFoundException("Choose an available property in your workspace.");
        var cases = VerticalCreativeTestPlanner.Plan(preview,
            mode, mode is VerticalCreativeTestMode.SingleFormat or VerticalCreativeTestMode.HookAb ? format : null);
        if (!await LockAsync(cancellationToken))
        {
            throw new InvalidOperationException("A test worker is updating the queue. Try again shortly.");
        }

        try
        {
            if (await dbContext.VerticalCreativeTestSuites.AnyAsync(suite => suite.OrganizationId == organizationId
                && (suite.Status == VerticalCreativeTestSuiteStatus.Queued || suite.Status == VerticalCreativeTestSuiteStatus.Running),
                cancellationToken))
            {
                throw new InvalidOperationException("A creative test is already running in this workspace. Cancel it or wait for it to finish.");
            }

            var suite = VerticalCreativeTestSuite.Create(organizationId, propertyId, userId, mode,
                mode is VerticalCreativeTestMode.SingleFormat or VerticalCreativeTestMode.HookAb ? format : null,
                preview.RuleVersion, cases, timeProvider.GetUtcNow(), captionStyle);
            dbContext.VerticalCreativeTestSuites.Add(suite);
            await dbContext.SaveChangesAsync(cancellationToken);
            var generated = VerticalCreativeTestPlanner.GeneratedCount(cases);
            LogSuiteStarted(suite.Id, mode, suite.Cases.Count, generated);
            return (await ToViewsAsync(organizationId, [suite], cancellationToken)).Single();
        }
        finally
        {
            await UnlockAsync();
        }
    }

    public async Task CancelAsync(string userId, Guid suiteId, CancellationToken cancellationToken = default)
    {
        var organizationId = await AuthorizeAsync(userId, cancellationToken);
        if (!await LockAsync(cancellationToken))
        {
            throw new InvalidOperationException("A test worker is updating the queue. Try again shortly.");
        }

        try
        {
            var suite = await dbContext.VerticalCreativeTestSuites.Include(candidate => candidate.Cases)
                .SingleOrDefaultAsync(candidate => candidate.Id == suiteId && candidate.OrganizationId == organizationId, cancellationToken)
                ?? throw new KeyNotFoundException("This creative test is unavailable in your workspace.");
            if (suite.RunningCase?.CampaignGenerationJobId is { } jobId)
            {
                var job = await dbContext.CampaignGenerationJobs.SingleOrDefaultAsync(candidate =>
                    candidate.Id == jobId && candidate.OrganizationId == organizationId, cancellationToken);
                if (job is not null && job.Status is CampaignGenerationStatus.Queued or CampaignGenerationStatus.Running)
                {
                    job.RequestCancellation(timeProvider.GetUtcNow());
                }
            }

            suite.Cancel("Cancelled. Finished cases and their videos are kept.", timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            await UnlockAsync();
        }
    }

    public async Task<IReadOnlyList<VerticalCreativeTestSuiteView>> ListAsync(string userId, CancellationToken cancellationToken = default)
    {
        var organizationId = await AuthorizeAsync(userId, cancellationToken);
        var suites = await dbContext.VerticalCreativeTestSuites.AsNoTracking().Include(suite => suite.Cases)
            .Where(suite => suite.OrganizationId == organizationId)
            .OrderByDescending(suite => suite.CreatedAtUtc).ThenByDescending(suite => suite.Id)
            .Take(50)
            .ToListAsync(cancellationToken);
        return await ToViewsAsync(organizationId, suites, cancellationToken);
    }

    public async Task ProcessNextAsync(CancellationToken cancellationToken = default)
    {
        if (!await LockAsync(cancellationToken))
        {
            return;
        }

        try
        {
            var candidates = await dbContext.VerticalCreativeTestSuites
                .Where(suite => suite.Status == VerticalCreativeTestSuiteStatus.Queued || suite.Status == VerticalCreativeTestSuiteStatus.Running)
                .OrderBy(suite => suite.UpdatedAtUtc).ThenBy(suite => suite.Id)
                .Select(suite => new { suite.Id, suite.OrganizationId })
                .Take(25)
                .ToListAsync(cancellationToken);
            foreach (var candidate in candidates)
            {
                // With the switch off for a workspace its waiting cases never start; they resume if it comes back on.
                if (!await featureStateService.IsEnabledAsync(FeatureFlagKeys.VerticalVideoLibraryV1, candidate.OrganizationId, cancellationToken))
                {
                    continue;
                }

                var suite = await dbContext.VerticalCreativeTestSuites.Include(item => item.Cases)
                    .SingleAsync(item => item.Id == candidate.Id, cancellationToken);
                await AdvanceAsync(suite, cancellationToken);
                await dbContext.SaveChangesAsync(cancellationToken);
                return;
            }
        }
        finally
        {
            await UnlockAsync();
        }
    }

    private async Task AdvanceAsync(VerticalCreativeTestSuite suite, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        try
        {
            var organizationId = await AuthorizeAdminAsync(suite.RequestedByUserId, cancellationToken);
            if (organizationId != suite.OrganizationId)
            {
                throw new UnauthorizedAccessException("The test owner's workspace changed.");
            }
        }
        catch (UnauthorizedAccessException exception)
        {
            suite.Cancel(exception.Message, now);
            return;
        }

        suite.MarkRunning(now);
        if (suite.RunningCase is { } running)
        {
            await CheckRunningCaseAsync(suite, running, cancellationToken);
        }
        else if (suite.NextCase is { } next)
        {
            await StartCaseAsync(suite, next, cancellationToken);
        }

        suite.CompleteIfDone(timeProvider.GetUtcNow());
    }

    private async Task StartCaseAsync(VerticalCreativeTestSuite suite, VerticalCreativeTestCase item, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var preview = await PreviewCoreAsync(suite.OrganizationId, suite.PropertyId, cancellationToken);
        if (preview is null)
        {
            item.Fail("The test property is no longer available.", null, null, now);
            return;
        }

        // The listing may have changed since the test was started: skip, never fail, a format it can't support now.
        var option = preview.Formats.Single(candidate => candidate.Format == item.Format);
        if (!option.IsEligible && !VerticalCreativeTestPlanner.AwaitsAnalysis(option, preview.PhotoAnalysisPending))
        {
            item.Skip($"This listing can no longer support {VerticalFormatNames.Name(item.Format)}.", now);
            return;
        }

        if (item.HookVariant == VerticalHookVariants.B && option.HookB is null && option.IsEligible)
        {
            item.Skip($"{VerticalFormatNames.Name(item.Format)} has no hook B that keeps to the verified facts for this listing.", now);
            return;
        }

        try
        {
            var queued = await campaigns.EnqueueCreativeTestAsync(suite.RequestedByUserId, suite.PropertyId, item.Format,
                item.HookVariant, item.CaseKey, suite.CaptionStyle, cancellationToken);
            if (queued is not null)
            {
                item.Start(queued.Id, timeProvider.GetUtcNow());
            }

            // Null: another campaign holds this property's active slot. Try again on a later pass.
        }
        catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException or UnauthorizedAccessException)
        {
            item.Fail(exception.Message, null, null, timeProvider.GetUtcNow());
        }
    }

    private async Task CheckRunningCaseAsync(VerticalCreativeTestSuite suite, VerticalCreativeTestCase item, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var job = await dbContext.CampaignGenerationJobs.AsNoTracking().SingleOrDefaultAsync(candidate =>
            candidate.Id == item.CampaignGenerationJobId && candidate.OrganizationId == suite.OrganizationId
            && candidate.PropertyId == suite.PropertyId, cancellationToken);
        if (job is null)
        {
            item.Fail("The test campaign is unavailable.", null, null, now);
            return;
        }

        if (job.Status is CampaignGenerationStatus.Queued or CampaignGenerationStatus.Running)
        {
            return;
        }

        if (job.Status != CampaignGenerationStatus.Completed)
        {
            item.Fail(job.Status == CampaignGenerationStatus.Cancelled
                ? "The test campaign was cancelled."
                : job.LastError ?? "The test campaign stopped before it finished.", null, null, now);
            return;
        }

        var variants = await dbContext.CampaignVideoLibraries.AsNoTracking()
            .Where(library => library.CampaignGenerationJobId == job.Id && library.OrganizationId == suite.OrganizationId)
            .SelectMany(library => library.Variants)
            .Where(variant => variant.Format == item.Format)
            .ToListAsync(cancellationToken);
        if (variants.Count == 0)
        {
            item.Skip($"When the campaign was planned this listing could no longer support {VerticalFormatNames.Name(item.Format)}, "
                + "so it made the shared vertical video instead.", now);
            return;
        }

        var variant = variants.FirstOrDefault(candidate => candidate.HookVariant == item.HookVariant);
        if (variant is null && item.HookVariant == VerticalHookVariants.B)
        {
            // Planned after photo analysis finished, the format had no hook B that keeps to the verified facts.
            item.Skip($"{VerticalFormatNames.Name(item.Format)} has no hook B that keeps to the verified facts for this listing.", now);
            return;
        }

        if (variant is null)
        {
            item.Fail($"The campaign made hook {variants[0].HookVariant.ToUpperInvariant()} instead of hook {item.HookVariant.ToUpperInvariant()}.",
                variants[0].Id, null, now);
            return;
        }

        if (variant.Status == CampaignVideoVariantStatus.Skipped)
        {
            item.Skip(variant.Error ?? $"This listing could not support {VerticalFormatNames.Name(item.Format)}.", now);
            return;
        }

        var deliverable = await dbContext.CampaignDeliverables.AsNoTracking().SingleOrDefaultAsync(candidate =>
            candidate.OrganizationId == suite.OrganizationId && candidate.CampaignVideoVariantId == variant.Id, cancellationToken);
        if (variant.Status != CampaignVideoVariantStatus.Rendered || deliverable is null)
        {
            item.Fail(variant.Error ?? "The video was not rendered.", variant.Id, deliverable?.Id, now);
            return;
        }

        var problems = await ValidateDeliverableAsync(item, variant, deliverable, cancellationToken);
        if (problems.Count > 0)
        {
            item.Fail(string.Join(" ", problems), variant.Id, deliverable.Id, timeProvider.GetUtcNow());
            return;
        }

        item.Pass(variant.Id, deliverable.Id, timeProvider.GetUtcNow());
    }

    /// <summary>Checks the case's own deliverable: recipe version, length, 9:16 frame, file health and a full decode.</summary>
    private async Task<IReadOnlyList<string>> ValidateDeliverableAsync(VerticalCreativeTestCase item, CampaignVideoVariant variant,
        CampaignDeliverable deliverable, CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        if (!string.Equals(variant.RecipeVersion, item.RecipeVersion, StringComparison.Ordinal))
        {
            problems.Add($"Recipe version {variant.RecipeVersion} was used, not {item.RecipeVersion}.");
        }

        if (variant.DurationSeconds != item.ExpectedDurationSeconds || (int)deliverable.RequestedDuration != item.ExpectedDurationSeconds)
        {
            problems.Add($"The video is {(int)deliverable.RequestedDuration} s, not the expected {item.ExpectedDurationSeconds} s.");
        }

        if (deliverable.AspectRatio != VideoAspectRatio.Vertical9By16)
        {
            problems.Add("The video is not 9:16.");
        }

        if (deliverable.Status != CampaignDeliverableStatus.Rendered || deliverable.FileSize is null or <= 0 || deliverable.AssetPath is null)
        {
            problems.Add("The video file is missing or empty.");
            return problems;
        }

        VideoProductionSpecification? specification;
        try
        {
            specification = JsonSerializer.Deserialize<VideoProductionSpecification>(deliverable.SpecificationJson, VideoSpecificationJson.Options);
        }
        catch (JsonException)
        {
            specification = null;
        }

        if (specification is null)
        {
            problems.Add("The video's specification could not be read.");
            return problems;
        }

        var path = Path.Combine(Path.GetTempPath(), $"creative-test-{Guid.NewGuid():N}.mp4");
        try
        {
            await using (var source = await assetStorage.OpenReadAsync(deliverable.AssetPath, cancellationToken))
            {
                if (source is null)
                {
                    problems.Add("The video file is missing from storage.");
                    return problems;
                }

                await using var target = File.Create(path);
                await source.CopyToAsync(target, cancellationToken);
            }

            if (new FileInfo(path).Length != deliverable.FileSize)
            {
                problems.Add("The stored file's size doesn't match the rendered size.");
            }

            var validation = await videoValidator.ValidateAsync(path, Mp4Expectations.For(specification), cancellationToken);
            if (!validation.IsValid)
            {
                problems.Add(validation.Summary);
            }
        }
        finally
        {
            File.Delete(path);
        }

        return problems;
    }

    private async Task<VerticalCreativeTestPreview?> PreviewCoreAsync(Guid organizationId, Guid propertyId, CancellationToken cancellationToken)
    {
        var property = await dbContext.Properties.AsNoTracking()
            .Where(candidate => candidate.Id == propertyId && candidate.OrganizationId == organizationId && candidate.ArchivedAtUtc == null)
            .Select(candidate => new { candidate.Address1, candidate.City, candidate.State })
            .SingleOrDefaultAsync(cancellationToken);
        if (property is null)
        {
            return null;
        }

        var plan = await planningService.PlanForOrganizationAsync(organizationId, propertyId,
            VerticalRecommendationContext.From(CampaignGenerationService.CreativeTestOptions), cancellationToken);
        return plan is null
            ? null
            : VerticalCreativeTestPlanner.Preview(propertyId, $"{property.Address1}, {property.City}, {property.State}", plan);
    }

    private async Task<IReadOnlyList<VerticalCreativeTestSuiteView>> ToViewsAsync(Guid organizationId,
        IReadOnlyList<VerticalCreativeTestSuite> suites, CancellationToken cancellationToken)
    {
        var propertyIds = suites.Select(suite => suite.PropertyId).Distinct().ToArray();
        var addresses = await dbContext.Properties.AsNoTracking()
            .Where(property => property.OrganizationId == organizationId && propertyIds.Contains(property.Id))
            .ToDictionaryAsync(property => property.Id, property => $"{property.Address1}, {property.City}, {property.State}", cancellationToken);
        var variantIds = suites.SelectMany(suite => suite.Cases).Select(item => item.CampaignVideoVariantId).OfType<Guid>().ToArray();
        var variants = await dbContext.CampaignVideoVariants.AsNoTracking()
            .Where(variant => variant.OrganizationId == organizationId && variantIds.Contains(variant.Id))
            .Select(variant => new { variant.Id, variant.Purpose, variant.HookText })
            .ToDictionaryAsync(variant => variant.Id, cancellationToken);
        var deliverableIds = suites.SelectMany(suite => suite.Cases).Select(item => item.CampaignDeliverableId).OfType<Guid>().ToArray();
        var versions = await dbContext.CampaignDeliverables.AsNoTracking()
            .Where(deliverable => deliverable.OrganizationId == organizationId && deliverableIds.Contains(deliverable.Id))
            .ToDictionaryAsync(deliverable => deliverable.Id, deliverable => deliverable.Version, cancellationToken);
        var reviews = await dbContext.CampaignVideoReviews.AsNoTracking()
            .Where(review => review.OrganizationId == organizationId && deliverableIds.Contains(review.CampaignDeliverableId))
            .OrderByDescending(review => review.Sequence)
            .ToListAsync(cancellationToken);
        return suites.Select(suite => new VerticalCreativeTestSuiteView(
            suite.Id, suite.PropertyId, addresses.GetValueOrDefault(suite.PropertyId, "Unavailable property"), suite.Mode, suite.Format,
            suite.RuleVersion, suite.Status, suite.Error, suite.CreatedAtUtc, suite.CompletedAtUtc,
            suite.Cases.OrderBy(item => item.Index).Select(item =>
            {
                var variant = item.CampaignVideoVariantId is { } variantId ? variants.GetValueOrDefault(variantId) : null;
                var version = item.CampaignDeliverableId is { } id ? versions.GetValueOrDefault(id) : 0;
                var history = reviews.Where(review => review.CampaignDeliverableId == item.CampaignDeliverableId)
                    .Select(review => new CampaignReviewEntry(review.Id, review.VideoVersion, review.Decision, review.Feedback, review.CreatedAtUtc))
                    .ToArray();
                return new VerticalCreativeTestCaseView(item.Id, item.Index, item.Format, item.HookVariant,
                    variant?.Purpose ?? VerticalFormatRules.PurposeOf(item.Format), item.Score,
                    VerticalCreativeTestPlanner.ReadReasons(item.ReasonsJson), item.RecipeVersion, item.ExpectedDurationSeconds,
                    item.Status, item.Error, item.CampaignGenerationJobId, item.CampaignVideoVariantId, item.CampaignDeliverableId,
                    variant?.HookText, version, history.FirstOrDefault(entry => entry.Version == version)?.Decision, history,
                    item.StartedAtUtc, item.FinishedAtUtc);
            }).ToArray(), suite.CaptionStyle)).ToArray();
    }

    private async Task<Guid> AuthorizeAsync(string userId, CancellationToken cancellationToken)
    {
        var organizationId = await AuthorizeAdminAsync(userId, cancellationToken);
        if (!await featureStateService.IsEnabledAsync(FeatureFlagKeys.VerticalVideoLibraryV1, organizationId, cancellationToken))
        {
            throw new CreativeTestingDisabledException();
        }

        return organizationId;
    }

    private async Task<Guid> AuthorizeAdminAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var user = await users.FindByIdAsync(userId);
        if (user is null || !await users.IsInRoleAsync(user, PlatformAdministration.Role))
        {
            throw new UnauthorizedAccessException("Platform administration access is required to run creative tests.");
        }

        return await dbContext.OrganizationMembers.AsNoTracking().Where(member => member.UserId == userId)
            .Select(member => (Guid?)member.OrganizationId).FirstOrDefaultAsync(cancellationToken)
            ?? throw new UnauthorizedAccessException("The administrator does not belong to a workspace.");
    }

    private async Task<bool> LockAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.Database.OpenConnectionAsync(cancellationToken);
            await using var command = dbContext.Database.GetDbConnection().CreateCommand();
            command.CommandText = $"SELECT pg_try_advisory_lock({LockKey})";
            if (await command.ExecuteScalarAsync(cancellationToken) is true)
            {
                return true;
            }

            await dbContext.Database.CloseConnectionAsync();
            return false;
        }
        catch
        {
            // Closing the connection releases a lock PostgreSQL granted before the cancellation arrived.
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
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Creative test {SuiteId} started: {Mode}, {CaseCount} cases, {GeneratedCount} to generate")]
    private partial void LogSuiteStarted(Guid suiteId, VerticalCreativeTestMode mode, int caseCount, int generatedCount);
}
