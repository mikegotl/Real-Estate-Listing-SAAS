namespace ListingStudio.Application.Properties;

public interface IPropertyNarrationScriptService
{
    const long MaximumFileSize = 2L * 1024 * 1024;
    const int MaximumExtractedCharacters = 12_000;
    const int ShortFormWordLimit = 145;
    const int MaximumAcceptedWords = 2_000;
    const int MaximumLongFormDurationSeconds = 900;

    Task<PropertyNarrationScriptItem?> GetAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default);

    Task<Guid> UploadAsync(
        string userId,
        Guid propertyId,
        PropertyNarrationScriptUpload upload,
        CancellationToken cancellationToken = default);

    Task<bool> SetMarketingUseAcceptedAsync(
        string userId,
        Guid propertyId,
        Guid scriptId,
        bool accepted,
        CancellationToken cancellationToken = default);

    Task<PropertyNarrationScriptContent?> OpenReadAsync(
        string userId,
        Guid scriptId,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        string userId,
        Guid propertyId,
        Guid scriptId,
        CancellationToken cancellationToken = default);
}

public sealed record PropertyNarrationScriptUpload(
    string OriginalFilename,
    string ContentType,
    long FileSize,
    Stream Content);

public sealed record PropertyNarrationScriptItem(
    Guid Id,
    string OriginalFilename,
    string ContentType,
    long FileSize,
    string ExtractedText,
    int WordCount,
    DateTimeOffset UploadedAtUtc,
    bool MarketingUseAccepted,
    DateTimeOffset? MarketingUseAcceptedAtUtc);

public sealed record PropertyNarrationScriptContent(
    Stream Content,
    string ContentType,
    string Filename);
