namespace ListingStudio.Application.Campaigns;

public interface IAgentPortraitService
{
    public const long MaximumFileSize = 10 * 1024 * 1024;

    bool IsAiFixEnabled { get; }

    Task<AgentPortraitItem?> GetLatestAsync(
        string userId,
        CancellationToken cancellationToken = default);

    Task<AgentPortraitItem> UploadAsync(
        string userId,
        AgentPortraitUpload upload,
        CancellationToken cancellationToken = default);

    Task<AgentPortraitItem?> CropAsync(
        string userId,
        Guid portraitId,
        AgentPortraitCrop crop,
        CancellationToken cancellationToken = default);

    Task<AgentPortraitItem?> AiFixAsync(
        string userId,
        Guid portraitId,
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

public enum AgentPortraitCropShape
{
    Square,
    PortraitFourByFive,
}

/// <summary>
/// A non-destructive crop instruction. Focus coordinates and zoom are normalized so the browser never needs to know
/// the stored image's pixel dimensions.
/// </summary>
public sealed record AgentPortraitCrop(
    decimal FocusX,
    decimal FocusY,
    decimal Zoom,
    AgentPortraitCropShape Shape)
{
    public void Validate()
    {
        if (FocusX is < 0 or > 1 || FocusY is < 0 or > 1 || Zoom is < 1 or > 4 || !Enum.IsDefined(Shape))
        {
            throw new ArgumentOutOfRangeException(nameof(AgentPortraitCrop), "Choose a valid crop and zoom level.");
        }
    }
}

public sealed record AgentPortraitItem(
    Guid Id,
    string OriginalFilename,
    int Width,
    int Height,
    DateTimeOffset UpdatedAtUtc);

public sealed record AgentPortraitContent(Stream Content, string ContentType);
