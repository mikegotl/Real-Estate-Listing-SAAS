namespace ListingStudio.Application.Campaigns;

public interface IAgentPortraitService
{
    public const long MaximumFileSize = 10 * 1024 * 1024;

    Task<AgentPortraitItem?> GetLatestAsync(
        string userId,
        CancellationToken cancellationToken = default);

    Task<AgentPortraitItem> UploadAsync(
        string userId,
        AgentPortraitUpload upload,
        CancellationToken cancellationToken = default);

    Task<AgentPortraitContent?> OpenReadAsync(
        string userId,
        Guid portraitId,
        CancellationToken cancellationToken = default);
}

public sealed record AgentPortraitUpload(
    string OriginalFilename,
    string ContentType,
    long FileSize,
    Stream Content);

public sealed record AgentPortraitItem(
    Guid Id,
    string OriginalFilename,
    int Width,
    int Height,
    DateTimeOffset UpdatedAtUtc);

public sealed record AgentPortraitContent(Stream Content, string ContentType);
