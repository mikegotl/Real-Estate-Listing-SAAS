using ListingStudio.Domain.Organizations;

namespace ListingStudio.Domain.Campaigns;

public sealed class AgentPortrait
{
    private AgentPortrait()
    {
    }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public string UserId { get; private set; } = string.Empty;
    public string BlobPath { get; private set; } = string.Empty;
    public string OriginalFilename { get; private set; } = string.Empty;
    public string MimeType { get; private set; } = string.Empty;
    public long FileSize { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public Organization Organization { get; private set; } = null!;

    public static AgentPortrait Create(
        Guid organizationId,
        string userId,
        string blobPath,
        string originalFilename,
        string mimeType,
        long fileSize,
        int width,
        int height,
        DateTimeOffset now)
    {
        if (organizationId == Guid.Empty || fileSize <= 0 || width <= 0 || height <= 0)
        {
            throw new ArgumentException("A valid organization and portrait image are required.");
        }

        return new AgentPortrait
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            UserId = Required(userId, 450, nameof(userId)),
            BlobPath = Required(blobPath, 1_024, nameof(blobPath)),
            OriginalFilename = Required(originalFilename, 255, nameof(originalFilename)),
            MimeType = Required(mimeType, 100, nameof(mimeType)),
            FileSize = fileSize,
            Width = width,
            Height = height,
            UpdatedAtUtc = now,
        };
    }

    private static string Required(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        return normalized.Length <= maximumLength
            ? normalized
            : throw new ArgumentOutOfRangeException(parameterName);
    }
}
