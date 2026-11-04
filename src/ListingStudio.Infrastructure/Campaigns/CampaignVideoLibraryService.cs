using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ListingStudio.Application.Audio;
using ListingStudio.Application.Campaigns;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ListingStudio.Infrastructure.Campaigns;

/// <summary>
/// Library results, per-video review and downloads for the signed-in user's workspace. Videos are addressed by
/// deliverable id and version; nothing here reads or changes another workspace's rows.
/// </summary>
public sealed class CampaignVideoLibraryService(
    ApplicationDbContext dbContext,
    ICampaignAssetStorage assetStorage,
    TimeProvider timeProvider) : ICampaignVideoLibraryService
{
    private static readonly JsonSerializerOptions ManifestOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<CampaignVideoLibraryView?> GetAsync(string userId, Guid jobId, CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var library = await LoadLibraryAsync(organizationId, jobId, cancellationToken);
        if (library is null)
        {
            return null;
        }

        var goal = await dbContext.CampaignGenerationJobs.AsNoTracking()
            .Where(job => job.Id == jobId && job.OrganizationId == organizationId)
            .Select(job => job.VerticalGoal)
            .SingleAsync(cancellationToken);
        var deliverableIds = library.Variants.Where(variant => variant.Deliverable is not null)
            .Select(variant => variant.Deliverable!.Id).ToArray();
        var reviews = await LoadReviewsAsync(organizationId, deliverableIds, cancellationToken);
        var snapshot = VerticalFormatRecommendationSet.FromSnapshotJson(library.RecommendationSnapshotJson);
        return new CampaignVideoLibraryView(
            library.CampaignGenerationJobId,
            library.Id,
            library.Status,
            library.Selection,
            goal,
            library.RuleVersion,
            library.Variants.OrderBy(variant => variant.Sequence)
                .Select(variant => ToView(variant, snapshot, reviews))
                .ToArray());
    }

    public async Task<CampaignVideoReviewState?> DecideAsync(
        string userId,
        Guid deliverableId,
        int expectedVersion,
        CampaignVideoReviewDecision decision,
        string? feedback,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var video = await dbContext.CampaignDeliverables.AsNoTracking()
            .Include(candidate => candidate.CampaignGenerationJob)
            .SingleOrDefaultAsync(candidate => candidate.Id == deliverableId && candidate.OrganizationId == organizationId,
                cancellationToken);
        if (video is null)
        {
            return null;
        }

        CampaignVideoReviewRules.EnsureReviewable(video, expectedVersion);
        dbContext.CampaignVideoReviews.Add(CampaignVideoReview.Create(
            organizationId, video.Id, expectedVersion, decision, userId, feedback, timeProvider.GetUtcNow()));
        await dbContext.SaveChangesAsync(cancellationToken);
        var reviews = await LoadReviewsAsync(organizationId, [video.Id], cancellationToken);
        return ReviewState(video.Id, video.Version, reviews);
    }

    public async Task<CampaignDownload?> OpenVideoAsync(
        string userId,
        Guid deliverableId,
        int? version,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var video = await dbContext.CampaignDeliverables.AsNoTracking()
            .Include(candidate => candidate.CampaignVideoVariant)
            .SingleOrDefaultAsync(candidate => candidate.Id == deliverableId
                && candidate.OrganizationId == organizationId
                && candidate.Status == CampaignDeliverableStatus.Rendered,
                cancellationToken);
        if (video?.AssetPath is null || video.ContentType is null || version is { } asked && asked != video.Version)
        {
            return null;
        }

        var content = await assetStorage.OpenReadAsync(video.AssetPath, cancellationToken);
        return content is null ? null : new CampaignDownload(content, video.ContentType, VideoFileName(video));
    }

    public async Task<CampaignDownload?> OpenCaptionAsync(string userId, Guid deliverableId, CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var video = await dbContext.CampaignDeliverables.AsNoTracking()
            .Include(candidate => candidate.CampaignVideoVariant)
            .SingleOrDefaultAsync(candidate => candidate.Id == deliverableId
                && candidate.OrganizationId == organizationId
                && candidate.Status == CampaignDeliverableStatus.Rendered,
                cancellationToken);
        var captions = VerticalVariantCaptions.FromJson(video?.CampaignVideoVariant?.CaptionsJson);
        if (video is null || captions is null)
        {
            return null;
        }

        return new CampaignDownload(
            new MemoryStream(Encoding.UTF8.GetBytes(CaptionText(captions))),
            "text/plain; charset=utf-8",
            CaptionFileName(video));
    }

    public async Task<CampaignLibraryArchive?> OpenArchiveAsync(string userId, Guid jobId, CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var library = await LoadLibraryAsync(organizationId, jobId, cancellationToken);
        var rendered = library?.Variants
            .Where(variant => variant.Status == CampaignVideoVariantStatus.Rendered
                && variant.Deliverable is { Status: CampaignDeliverableStatus.Rendered, AssetPath: not null })
            .OrderBy(variant => variant.Sequence)
            .ToArray() ?? [];
        if (library is null || rendered.Length == 0)
        {
            return null;
        }

        var goal = await dbContext.CampaignGenerationJobs.AsNoTracking()
            .Where(job => job.Id == jobId && job.OrganizationId == organizationId)
            .Select(job => job.VerticalGoal)
            .SingleAsync(cancellationToken);
        var reviews = await LoadReviewsAsync(organizationId, rendered.Select(variant => variant.Deliverable!.Id).ToArray(), cancellationToken);
        var manifest = BuildManifest(library, goal, rendered, reviews, timeProvider.GetUtcNow());
        var propertyPrefix = library.PropertyId.ToString("N")[..8];
        return new CampaignLibraryArchive(
            $"{propertyPrefix}-vertical-video-library.zip",
            async (output, token) =>
            {
                // The ZIP is built in a temporary file first: writing a ZIP straight to a response stream makes
                // synchronous writes, which the web server refuses.
                await using var buffer = new FileStream(
                    Path.Combine(Path.GetTempPath(), $"library-{Guid.NewGuid():N}.zip"),
                    FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81_920,
                    FileOptions.Asynchronous | FileOptions.DeleteOnClose);
                await WriteArchiveAsync(buffer, rendered, manifest, token);
                buffer.Position = 0;
                await buffer.CopyToAsync(output, token);
            });
    }

    private async Task WriteArchiveAsync(
        Stream output,
        IReadOnlyList<CampaignVideoVariant> rendered,
        CampaignLibraryManifest manifest,
        CancellationToken token)
    {
        await using var archive = await ZipArchive.CreateAsync(output, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: null, token);
        foreach (var variant in rendered)
        {
            var video = variant.Deliverable!;
            await using (var source = await assetStorage.OpenReadAsync(video.AssetPath!, token)
                ?? throw new FileNotFoundException("A library video file is missing from storage."))
            {
                // Video is already compressed; storing it keeps the download fast.
                var entry = archive.CreateEntry(VideoFileName(video), CompressionLevel.NoCompression);
                await using var target = await entry.OpenAsync(token);
                await source.CopyToAsync(target, token);
            }

            if (VerticalVariantCaptions.FromJson(variant.CaptionsJson) is { } captions)
            {
                var entry = archive.CreateEntry(CaptionFileName(video), CompressionLevel.Optimal);
                await using var target = await entry.OpenAsync(token);
                await target.WriteAsync(Encoding.UTF8.GetBytes(CaptionText(captions)), token);
            }
        }

        var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
        await using var manifestStream = await manifestEntry.OpenAsync(token);
        await JsonSerializer.SerializeAsync(manifestStream, manifest, ManifestOptions, token);
    }

    /// <summary>The manifest: what each file is and how it was checked. No URLs, storage paths, ids or secrets.</summary>
    internal static CampaignLibraryManifest BuildManifest(
        CampaignVideoLibrary library,
        VerticalCampaignGoal? goal,
        IReadOnlyList<CampaignVideoVariant> rendered,
        IReadOnlyList<CampaignVideoReview> reviews,
        DateTimeOffset now) =>
        new(
            CampaignLibraryManifest.CurrentVersion,
            library.RuleVersion ?? string.Empty,
            library.Selection.ToString(),
            goal?.ToString(),
            now,
            rendered.Select(variant =>
            {
                var video = variant.Deliverable!;
                var decision = reviews
                    .Where(review => review.CampaignDeliverableId == video.Id && review.VideoVersion == video.Version)
                    .OrderByDescending(review => review.Sequence)
                    .FirstOrDefault()?.Decision;
                return new CampaignLibraryManifestEntry(
                    variant.Format.ToString(),
                    variant.Purpose.ToString(),
                    variant.HookText,
                    variant.CallToAction,
                    VideoFileName(video),
                    CaptionFileName(video),
                    video.Version,
                    variant.DurationSeconds,
                    variant.RecipeVersion,
                    CampaignLibraryVideoView.IntendedPlatforms,
                    "passed",
                    decision?.ToString() ?? "NotReviewed");
            }).ToArray());

    internal static string VideoFileName(CampaignDeliverable video) => $"{BaseName(video)}.mp4";

    internal static string CaptionFileName(CampaignDeliverable video) => $"{BaseName(video)}-caption.txt";

    internal static string CaptionText(VerticalVariantCaptions captions) =>
        $"{captions.ShortCaption}\n\n{captions.LongCaption}\n";

    // A non-sensitive name: the first 8 characters of the property id, the format and the version.
    private static string BaseName(CampaignDeliverable video)
    {
        var name = video.CampaignVideoVariant is { } variant && variant.Id != video.Id
            ? Kebab(variant.Format.ToString())
            : Kebab(video.Kind.ToString());
        return $"{video.PropertyId.ToString("N")[..8]}-{name}-v{video.Version}";
    }

    private static string Kebab(string value) => string.Concat(value.Select((character, index) =>
        index > 0 && char.IsUpper(character) ? $"-{char.ToLowerInvariant(character)}" : char.ToLowerInvariant(character).ToString()));

    private static CampaignLibraryVideoView ToView(
        CampaignVideoVariant variant,
        VerticalFormatRecommendationSet? snapshot,
        IReadOnlyList<CampaignVideoReview> reviews)
    {
        var recommendation = snapshot?.Formats.FirstOrDefault(item => item.Format == variant.Format);
        var video = variant.Deliverable;
        var technical = variant.Status switch
        {
            CampaignVideoVariantStatus.Rendered when video?.Status == CampaignDeliverableStatus.Rendered => CampaignVideoTechnicalStatus.Passed,
            CampaignVideoVariantStatus.Failed => CampaignVideoTechnicalStatus.Failed,
            CampaignVideoVariantStatus.Skipped => CampaignVideoTechnicalStatus.Skipped,
            CampaignVideoVariantStatus.Cancelled => CampaignVideoTechnicalStatus.Cancelled,
            _ => CampaignVideoTechnicalStatus.Waiting,
        };
        return new CampaignLibraryVideoView(
            variant.Id,
            video?.Id,
            variant.Format,
            variant.Purpose,
            variant.Sequence,
            recommendation?.Score,
            recommendation?.Strength,
            recommendation?.Reasons ?? [],
            variant.HookText,
            variant.CallToAction,
            variant.DurationSeconds,
            variant.RecipeVersion,
            variant.Status,
            technical,
            variant.Status == CampaignVideoVariantStatus.Failed ? variant.Error : null,
            video?.Version ?? 0,
            video is null ? CampaignVideoReviewState.None(0) : ReviewState(video.Id, video.Version, reviews),
            VerticalVariantCaptions.FromJson(variant.CaptionsJson),
            variant.KlingProviderRequestId);
    }

    private static CampaignVideoReviewState ReviewState(Guid deliverableId, int version, IReadOnlyList<CampaignVideoReview> reviews)
    {
        var history = reviews.Where(review => review.CampaignDeliverableId == deliverableId)
            .OrderByDescending(review => review.Sequence)
            .ToArray();
        var current = history.FirstOrDefault(review => review.VideoVersion == version);
        return new CampaignVideoReviewState(
            version,
            current?.Decision,
            current?.Feedback,
            history.Select(review => new CampaignReviewEntry(review.Id, review.VideoVersion, review.Decision, review.Feedback,
                review.CreatedAtUtc)).ToArray());
    }

    private Task<CampaignVideoLibrary?> LoadLibraryAsync(Guid organizationId, Guid jobId, CancellationToken cancellationToken) =>
        dbContext.CampaignVideoLibraries.AsNoTracking()
            .Include(library => library.Variants)
            .ThenInclude(variant => variant.Deliverable)
            .Where(library => library.OrganizationId == organizationId
                && library.CampaignGenerationJobId == jobId
                // Libraries backfilled from past campaigns hold the one shared video; those keep the legacy view.
                && library.Selection != VerticalVideoSelection.LegacySingle)
            .SingleOrDefaultAsync(cancellationToken);

    private async Task<IReadOnlyList<CampaignVideoReview>> LoadReviewsAsync(
        Guid organizationId,
        Guid[] deliverableIds,
        CancellationToken cancellationToken) =>
        deliverableIds.Length == 0
            ? []
            : await dbContext.CampaignVideoReviews.AsNoTracking()
                .Where(review => review.OrganizationId == organizationId && deliverableIds.Contains(review.CampaignDeliverableId))
                .ToListAsync(cancellationToken);

    private async Task<Guid> GetOrganizationIdAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return await dbContext.OrganizationMembers.AsNoTracking()
            .Where(member => member.UserId == userId)
            .Select(member => (Guid?)member.OrganizationId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new UnauthorizedAccessException("The user does not belong to an organization.");
    }
}

/// <summary>The rules every review follows, from the agent's library and from the admin review studio alike.</summary>
public static class CampaignVideoReviewRules
{
    public static void EnsureReviewable(CampaignDeliverable video, int expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(video);
        if (video.Version != expectedVersion)
        {
            throw new InvalidOperationException("The video changed. Refresh and watch the current version before reviewing it.");
        }

        if (video.Status != CampaignDeliverableStatus.Rendered
            || video.CampaignGenerationJob.Status != CampaignGenerationStatus.Completed)
        {
            throw new InvalidOperationException("Wait for the campaign to complete before reviewing its video.");
        }
    }
}
