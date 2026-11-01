using ListingStudio.Domain.Campaigns;

namespace ListingStudio.Application.Campaigns;

public interface ITikTokTestSuiteService
{
    Task<IReadOnlyList<TikTokTestPropertyChoice>> PropertyChoicesAsync(string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TikTokTestSuiteResult>> ListAsync(string userId, CancellationToken cancellationToken = default);
    Task<TikTokTestSuiteResult> StartAsync(string userId, Guid propertyId, CancellationToken cancellationToken = default);
    Task SetArchivedAsync(string userId, Guid suiteId, bool archived, CancellationToken cancellationToken = default);
    Task CancelAsync(string userId, Guid suiteId, CancellationToken cancellationToken = default);
}

public interface ITikTokTestSuiteProcessor
{
    Task ProcessNextAsync(CancellationToken cancellationToken = default);
}

/// <summary>Fresh test runs use an idempotent case key instead of reusing an earlier completed campaign.</summary>
public interface ICampaignTestGenerationService
{
    Task<CampaignGenerationResult?> EnqueueTestAsync(string userId, Guid propertyId,
        CampaignCreativeOptions options, Guid caseRequestId, CancellationToken cancellationToken = default);

    /// <summary>
    /// One creative format test case: a private TikTok campaign that makes exactly one vertical library video in the
    /// given format, with hook "a" or "b". Needs VerticalVideoLibraryV1 on for the workspace; null when the property
    /// is unavailable or another campaign holds the property's active slot (try again later).
    /// </summary>
    Task<CampaignGenerationResult?> EnqueueCreativeTestAsync(string userId, Guid propertyId,
        VerticalCreativeFormat format, string hookVariant, Guid caseRequestId,
        CampaignCaptionStyle captionStyle = CampaignCaptionStyles.Default, CancellationToken cancellationToken = default);
}

public sealed record TikTokTestPropertyChoice(Guid Id, string Address);

public sealed record TikTokTestSuiteResult(Guid Id, Guid PropertyId, string Address,
    TikTokTestSuiteStatus Status, int CurrentCaseIndex, string? Error,
    IReadOnlyList<Guid?> CaseJobIds, DateTimeOffset CreatedAtUtc, DateTimeOffset? ArchivedAtUtc = null);
