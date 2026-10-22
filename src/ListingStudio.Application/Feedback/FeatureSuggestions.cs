using ListingStudio.Domain.Feedback;

namespace ListingStudio.Application.Feedback;

public sealed record FeatureSuggestionInput(
    string Comment,
    bool RequiredForActiveCampaign);

public sealed record FeatureSuggestionReceipt(
    Guid Id,
    Guid PropertyId,
    bool RequiredForActiveCampaign,
    Guid? CampaignGenerationJobId,
    DateTimeOffset SubmittedAtUtc);

public interface IFeatureSuggestionService
{
    Task<FeatureSuggestionReceipt?> SubmitAsync(
        string userId,
        Guid propertyId,
        FeatureSuggestionInput input,
        CancellationToken cancellationToken = default);
}
