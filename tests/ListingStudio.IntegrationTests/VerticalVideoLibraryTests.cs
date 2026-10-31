using ListingStudio.Application.Administration;
using ListingStudio.Application.Campaigns;
using ListingStudio.Domain.Administration;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Infrastructure.Administration;
using ListingStudio.Infrastructure.Persistence;
using ListingStudio.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed partial class CampaignGenerationTests
{
    [Fact]
    public async Task FeatureSwitchIsOffByDefaultAndTheKillSwitchOverridesThePilotList()
    {
        var admin = await CreateOwnerAndPropertyAsync("flag-admin");
        var pilot = await CreateOwnerAndPropertyAsync("flag-pilot");
        var other = await CreateOwnerAndPropertyAsync("flag-other");
        await GrantReviewAdminAsync(admin.UserId);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var features = scope.ServiceProvider.GetRequiredService<IFeatureStateService>();
        const string key = FeatureFlagKeys.VerticalVideoLibraryV1;
        try
        {
            await features.ConfigureAsync(admin.UserId, key, false, false, []);
            Assert.False(await features.IsEnabledAsync(key, pilot.OrganizationId));

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                features.ConfigureAsync(pilot.UserId, key, true, true, []));
            await Assert.ThrowsAsync<ArgumentException>(() => features.GetAsync("SomethingElse"));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                features.ConfigureAsync(admin.UserId, key, true, false, [Guid.NewGuid()]));

            // Pilot: on only for the listed workspace.
            await features.ConfigureAsync(admin.UserId, key, true, false, [pilot.OrganizationId]);
            Assert.True(await features.IsEnabledAsync(key, pilot.OrganizationId));
            Assert.False(await features.IsEnabledAsync(key, other.OrganizationId));

            // Global off wins over the pilot list, and takes effect at once in this host.
            await features.ConfigureAsync(admin.UserId, key, false, false, [pilot.OrganizationId]);
            Assert.False(await features.IsEnabledAsync(key, pilot.OrganizationId));
            var state = await features.GetAsync(key);
            Assert.Equal([pilot.OrganizationId], state.PilotOrganizationIds);

            // Everyone, then the kill switch again.
            await features.ConfigureAsync(admin.UserId, key, true, true, []);
            Assert.True(await features.IsEnabledAsync(key, other.OrganizationId));
            await features.ConfigureAsync(admin.UserId, key, false, true, []);
            Assert.False(await features.IsEnabledAsync(key, other.OrganizationId));
        }
        finally
        {
            await features.ConfigureAsync(admin.UserId, key, false, false, []);
        }
    }

    [Fact]
    public async Task FeatureSwitchChangedByAnotherHostIsSeenWithinTheCacheLifetime()
    {
        var owner = await CreateOwnerAndPropertyAsync("flag-cache");
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var cache = new FeatureStateCache();
        var clock = fixture.Factory.TimeProvider;
        var service = new FeatureStateService(
            db, scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<
                ListingStudio.Infrastructure.Identity.ApplicationUser>>(), cache, new ShiftedClock(clock, TimeSpan.Zero));
        const string key = FeatureFlagKeys.VerticalVideoLibraryV1;
        var before = await service.IsEnabledAsync(key, owner.OrganizationId);

        // Another host turns the feature on for everyone directly in the database.
        var flag = await db.FeatureFlags.Include(item => item.Organizations).SingleOrDefaultAsync(item => item.Key == key);
        if (flag is null)
        {
            flag = FeatureFlag.Create(key, clock.GetUtcNow());
            db.FeatureFlags.Add(flag);
        }

        var wasGlobal = flag.IsGloballyEnabled;
        var wasAll = flag.IsEnabledForAllOrganizations;
        flag.Configure(true, true, null, clock.GetUtcNow());
        await db.SaveChangesAsync();
        try
        {
            Assert.Equal(before, await service.IsEnabledAsync(key, owner.OrganizationId));
            var later = new FeatureStateService(
                db, scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<
                    ListingStudio.Infrastructure.Identity.ApplicationUser>>(), cache,
                new ShiftedClock(clock, IFeatureStateService.CacheLifetime + TimeSpan.FromSeconds(1)));
            Assert.True(await later.IsEnabledAsync(key, owner.OrganizationId));
        }
        finally
        {
            flag.Configure(wasGlobal, wasAll, null, clock.GetUtcNow());
            await db.SaveChangesAsync();
            scope.ServiceProvider.GetRequiredService<FeatureStateCache>().Invalidate(key);
        }
    }

    [Fact]
    public async Task LibraryStoresSeveralVerticalVideosForOneCampaignWithinItsWorkspace()
    {
        var owner = await CreateOwnerAndPropertyAsync("library-model");
        var outsider = await CreateOwnerAndPropertyAsync("library-outsider");
        await UploadAsync(owner);
        QueueSuccessfulProviders();
        fixture.Factory.VoiceProvider.Enqueue(CreateSegmentedVoiceResult);
        var queued = await EnqueueAsync(owner, CampaignOutputSelection.TikTok);
        await ProcessUntilAsync(owner, queued.Id, CampaignGenerationStatus.Completed);
        var now = fixture.Factory.TimeProvider.GetUtcNow();

        Guid libraryId;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var library = CampaignVideoLibrary.Create(owner.OrganizationId, owner.PropertyId, queued.Id,
                VerticalVideoSelection.RecommendedThree, "vertical-rules-v1", """{"formats":["WowFeature"]}""", now);
            var wow = library.AddVariant(Plan(VerticalCreativeFormat.WowFeature, 1, 15), now);
            library.AddVariant(Plan(VerticalCreativeFormat.PriceFirst, 2, 30), now);
            library.AddVariant(Plan(VerticalCreativeFormat.StoryReveal, 3, 45, eligible: false), now);
            db.CampaignVideoLibraries.Add(library);
            await db.SaveChangesAsync();
            libraryId = library.Id;

            // A second vertical render in the same campaign is allowed once it belongs to a library video.
            var legacy = await db.CampaignDeliverables.AsNoTracking()
                .SingleAsync(item => item.CampaignGenerationJobId == queued.Id && item.Kind == CampaignOutputKind.TikTok);
            var extra = CampaignDeliverable.Create(queued.Id, owner.OrganizationId, owner.PropertyId,
                legacy.VideoNarrationId, CampaignOutputKind.TikTok, legacy.RequestedDuration, legacy.AspectRatio,
                legacy.SpecificationJson, now);
            extra.AttachToVariant(wow.Id);
            db.CampaignDeliverables.Add(extra);
            await db.SaveChangesAsync();
            Assert.Throws<InvalidOperationException>(() => extra.AttachToVariant(Guid.NewGuid()));

            // Outside the library the one-per-kind rule still holds.
            db.CampaignDeliverables.Add(CampaignDeliverable.Create(queued.Id, owner.OrganizationId, owner.PropertyId,
                legacy.VideoNarrationId, CampaignOutputKind.TikTok, legacy.RequestedDuration, legacy.AspectRatio,
                legacy.SpecificationJson, now));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var loaded = await db.CampaignVideoLibraries.AsNoTracking()
                .Include(library => library.Variants).ThenInclude(variant => variant.Deliverable)
                .SingleAsync(library => library.Id == libraryId && library.OrganizationId == owner.OrganizationId);
            Assert.Equal(
                [VerticalCreativeFormat.WowFeature, VerticalCreativeFormat.PriceFirst, VerticalCreativeFormat.StoryReveal],
                loaded.Variants.OrderBy(variant => variant.Sequence).Select(variant => variant.Format));
            Assert.All(loaded.Variants, variant => Assert.Equal(owner.OrganizationId, variant.OrganizationId));
            Assert.Equal(CampaignVideoVariantStatus.Skipped,
                loaded.Variants.Single(variant => variant.Format == VerticalCreativeFormat.StoryReveal).Status);
            Assert.NotNull(loaded.Variants.Single(variant => variant.Format == VerticalCreativeFormat.WowFeature).Deliverable);
            Assert.Equal("""{"formats": ["WowFeature"]}""", loaded.RecommendationSnapshotJson);

            // Workspace scoping: another workspace never sees the library or its videos.
            Assert.False(await db.CampaignVideoLibraries.AnyAsync(library =>
                library.Id == libraryId && library.OrganizationId == outsider.OrganizationId));
            Assert.False(await db.CampaignVideoVariants.AnyAsync(variant =>
                variant.CampaignVideoLibraryId == libraryId && variant.OrganizationId == outsider.OrganizationId));

            // Format and position are unique within a library even when the domain check is bypassed.
            var detached = await db.CampaignVideoLibraries.SingleAsync(library => library.Id == libraryId);
            detached.AddVariant(Plan(VerticalCreativeFormat.WowFeature, 1, 15), now);
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            // Deleting the campaign removes its library and videos with it.
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var job = await db.CampaignGenerationJobs.SingleAsync(candidate => candidate.Id == queued.Id);
            db.CampaignGenerationJobs.Remove(job);
            await db.SaveChangesAsync();
            Assert.False(await db.CampaignVideoLibraries.AnyAsync(library => library.Id == libraryId));
            Assert.False(await db.CampaignVideoVariants.AnyAsync(variant => variant.CampaignVideoLibraryId == libraryId));
        }
    }

    [Fact]
    public async Task BackfillTurnsPastVerticalVideosIntoLegacyLibrariesAndTheLegacyPathStillWorks()
    {
        var owner = await CreateOwnerAndPropertyAsync("library-backfill");
        await GrantReviewAdminAsync(owner.UserId);
        await UploadAsync(owner);
        QueueSuccessfulProviders();
        fixture.Factory.VoiceProvider.Enqueue(CreateSegmentedVoiceResult);
        var first = await EnqueueAsync(owner, CampaignOutputSelection.TikTok);
        await ProcessUntilAsync(owner, first.Id, CampaignGenerationStatus.Completed);

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.ExecuteSqlRawAsync(AddVerticalVideoLibrary.BackfillSql);
            await db.Database.ExecuteSqlRawAsync(AddVerticalVideoLibrary.BackfillSql);

            var library = await db.CampaignVideoLibraries.AsNoTracking().Include(item => item.Variants)
                .SingleAsync(item => item.CampaignGenerationJobId == first.Id);
            Assert.Equal((VerticalVideoSelection.LegacySingle, CampaignVideoLibraryStatus.Completed),
                (library.Selection, library.Status));
            Assert.Equal(owner.OrganizationId, library.OrganizationId);
            var variant = Assert.Single(library.Variants);
            Assert.Equal((VerticalCreativeFormat.LegacySingle, 1, CampaignVideoVariantStatus.Rendered),
                (variant.Format, variant.Sequence, variant.Status));
            var deliverables = await db.CampaignDeliverables.AsNoTracking()
                .Where(item => item.CampaignGenerationJobId == first.Id).ToListAsync();
            // Only the vertical output is linked, and it keeps its id, kind, file and version.
            var tiktok = Assert.Single(deliverables, item => item.Kind == CampaignOutputKind.TikTok);
            Assert.Equal(variant.Id, tiktok.Id);
            Assert.Equal(variant.Id, tiktok.CampaignVideoVariantId);
            Assert.All(deliverables.Where(item => item.Kind != CampaignOutputKind.TikTok),
                item => Assert.Null(item.CampaignVideoVariantId));
        }

        // With the feature off and library records in the database, the legacy campaign path, the review gallery
        // and saved settings behave exactly as before.
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var features = scope.ServiceProvider.GetRequiredService<IFeatureStateService>();
            Assert.False(await features.IsEnabledAsync(FeatureFlagKeys.VerticalVideoLibraryV1, owner.OrganizationId));
            var reviews = scope.ServiceProvider.GetRequiredService<ICampaignReviewService>();
            var item = Assert.Single(await reviews.ListAsync(owner.UserId), candidate => candidate.JobId == first.Id);
            await reviews.DecideAsync(owner.UserId, first.Id, item.Version, CampaignVideoReviewDecision.Approved, null);
        }

        // A new campaign on another listing (fresh analysis and story) still takes the legacy path end to end.
        var next = await CreateOwnerAndPropertyAsync("library-backfill-next");
        await UploadAsync(next);
        QueueSuccessfulProviders();
        fixture.Factory.VoiceProvider.Enqueue(CreateSegmentedVoiceResult);
        var second = await EnqueueAsync(next, CampaignOutputSelection.TikTok);
        await ProcessUntilAsync(next, second.Id, CampaignGenerationStatus.Completed);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var tiktok = await db.CampaignDeliverables.AsNoTracking()
                .SingleAsync(item => item.CampaignGenerationJobId == second.Id && item.Kind == CampaignOutputKind.TikTok);
            Assert.Null(tiktok.CampaignVideoVariantId);
            Assert.False(await db.CampaignVideoLibraries.AnyAsync(library => library.CampaignGenerationJobId == second.Id));
        }
    }

    private static CampaignVideoVariantPlan Plan(
        VerticalCreativeFormat format, int sequence, int seconds, bool eligible = true) =>
        new(format, VerticalCampaignPurpose.Reach, sequence, seconds, eligible, "Listed at $450,000",
            "property.listingPrice", null, CampaignVoiceoverMode.ShortHook, "Book a showing",
            """{"scenes":[]}""", "recipe-v1");

    private sealed class ShiftedClock(TimeProvider inner, TimeSpan shift) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow() + shift;
    }
}
