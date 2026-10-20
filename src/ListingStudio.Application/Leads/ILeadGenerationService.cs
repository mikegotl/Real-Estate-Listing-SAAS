using ListingStudio.Domain.Leads;

namespace ListingStudio.Application.Leads;

public interface ILeadGenerationService
{
    public const string ConsentVersion = "2026-10";

    Task<LeadCaptureSettings?> GetCaptureSettingsAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default);

    Task<LeadCaptureSettings?> SaveCaptureSettingsAsync(
        string userId,
        Guid propertyId,
        string? headline,
        bool published,
        CancellationToken cancellationToken = default);

    Task<PublicListingResult?> GetPublicListingAsync(
        string publicSlug,
        CancellationToken cancellationToken = default);

    Task<PublicLeadSubmissionResult> SubmitAsync(
        string publicSlug,
        PublicLeadInput input,
        CancellationToken cancellationToken = default);

    Task<LeadDashboard> GetDashboardAsync(
        string userId,
        CancellationToken cancellationToken = default);

    Task<LeadDetails?> GetLeadAsync(
        string userId,
        Guid leadId,
        CancellationToken cancellationToken = default);

    Task<bool> UpdateWorkflowAsync(
        string userId,
        Guid leadId,
        LeadStatus status,
        DateTimeOffset? followUpAtUtc,
        CancellationToken cancellationToken = default);

    Task<bool> AddNoteAsync(
        string userId,
        Guid leadId,
        string note,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        string userId,
        Guid leadId,
        CancellationToken cancellationToken = default);

    Task<LeadPublicAsset?> OpenCoverAsync(
        string publicSlug,
        CancellationToken cancellationToken = default);

    Task<LeadPublicAsset?> OpenVideoAsync(
        string publicSlug,
        CancellationToken cancellationToken = default);

    Task<byte[]> ExportCsvAsync(
        string userId,
        CancellationToken cancellationToken = default);
}

public sealed record LeadCaptureSettings(
    Guid PropertyId,
    string PropertyLabel,
    string PublicSlug,
    string? Headline,
    bool IsPublished,
    DateTimeOffset UpdatedAtUtc);

public sealed record PublicListingResult(
    string PublicSlug,
    string OrganizationName,
    string Address1,
    string? Address2,
    string City,
    string State,
    string ZipCode,
    decimal ListingPrice,
    int Bedrooms,
    decimal Bathrooms,
    int? SquareFeet,
    decimal? LotSize,
    int? YearBuilt,
    string PropertyType,
    string? Description,
    string? Headline,
    bool HasCover,
    bool HasVideo);

public sealed record PublicLeadInput(
    string Name,
    string Email,
    string? Phone,
    LeadPreferredContact PreferredContact,
    string Message,
    bool EmailMarketingConsent,
    bool SmsMarketingConsent,
    string? Source,
    string? Medium,
    string? Campaign,
    string? Website);

public sealed record PublicLeadSubmissionResult(bool Succeeded, Guid? LeadId, IReadOnlyList<string> Errors)
{
    public static PublicLeadSubmissionResult Success(Guid leadId) => new(true, leadId, []);

    public static PublicLeadSubmissionResult Failure(params string[] errors) => new(false, null, errors);
}

public sealed record LeadDashboard(
    int Total,
    int New,
    int FollowUpsDue,
    int ShowingScheduled,
    int Won,
    IReadOnlyList<LeadSummary> Leads);

public sealed record LeadSummary(
    Guid Id,
    Guid PropertyId,
    string PropertyLabel,
    string Name,
    string Email,
    string? Phone,
    LeadPreferredContact PreferredContact,
    LeadStatus Status,
    string? Source,
    DateTimeOffset? FollowUpAtUtc,
    DateTimeOffset CreatedAtUtc);

public sealed record LeadDetails(
    Guid Id,
    Guid PropertyId,
    string PropertyLabel,
    string Name,
    string Email,
    string? Phone,
    LeadPreferredContact PreferredContact,
    string Message,
    LeadStatus Status,
    string? Source,
    string? Medium,
    string? Campaign,
    bool EmailMarketingConsent,
    bool SmsMarketingConsent,
    string ConsentVersion,
    DateTimeOffset? FollowUpAtUtc,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<LeadActivityItem> Activities);

public sealed record LeadActivityItem(
    LeadActivityKind Kind,
    string Text,
    DateTimeOffset CreatedAtUtc);

public sealed record LeadPublicAsset(Stream Content, string ContentType);
