using ListingStudio.Domain.Feedback;

namespace ListingStudio.Application.Feedback;

public sealed record FeatureSuggestionInput(
    string Comment,
    FeatureSuggestionType Type,
    bool RequiredForActiveCampaign,
    string? PagePath = null);

public sealed record FeatureSuggestionReceipt(
    Guid Id,
    Guid? PropertyId,
    FeatureSuggestionType Type,
    bool RequiredForActiveCampaign,
    Guid? CampaignGenerationJobId,
    DateTimeOffset SubmittedAtUtc);

public interface IFeatureSuggestionService
{
    /// <summary>
    /// Saves a comment from a signed-in user. With a property id the comment is linked to that property, and null is
    /// returned when the property is not in the user's organization; without one it is a general comment.
    /// </summary>
    Task<FeatureSuggestionReceipt?> SubmitAsync(
        string userId,
        Guid? propertyId,
        FeatureSuggestionInput input,
        CancellationToken cancellationToken = default);
}

public enum FeatureSuggestionEmailOutcome
{
    Sent,
    NoRecipients,
    Failed,
}

public sealed record FeatureSuggestionEmailRunResult(
    Guid SuggestionId,
    FeatureSuggestionEmailOutcome Outcome,
    int RecipientCount,
    int AttemptNumber,
    bool WillRetry,
    string? Error);

/// <summary>Emails the next comment that platform admins have not been sent yet.</summary>
public interface IFeatureSuggestionEmailProcessor
{
    /// <summary>Returns null when no comment is waiting.</summary>
    Task<FeatureSuggestionEmailRunResult?> ProcessNextAsync(CancellationToken cancellationToken = default);
}
