using System.Text.Json;
using System.Text.Json.Serialization;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Properties;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ListingStudio.Infrastructure.Campaigns;

/// <summary>The edits a chat message proposed, with the plain description of each that the user saw.</summary>
internal sealed record CampaignVideoProposal(IReadOnlyList<CampaignVideoEdit> Edits, IReadOnlyList<string> Changes);

internal static class CampaignVideoEditing
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(CampaignVideoProposal proposal) =>
        JsonSerializer.Serialize(proposal, SerializerOptions);

    public static CampaignVideoProposal Deserialize(string json) =>
        JsonSerializer.Deserialize<CampaignVideoProposal>(json, SerializerOptions)
            ?? throw new InvalidDataException("Stored video edits are invalid.");

    /// <summary>The listing photos a revision may use and how long each walkthrough video runs.</summary>
    public static async Task<CampaignVideoEditContext> LoadContextAsync(
        ApplicationDbContext dbContext,
        Guid organizationId,
        Guid propertyId,
        CancellationToken cancellationToken)
    {
        var photos = await dbContext.PropertyMedia.AsNoTracking()
            .Where(candidate => candidate.OrganizationId == organizationId
                && candidate.PropertyId == propertyId
                && !candidate.IsInGallery)
            .OrderBy(candidate => candidate.DisplayOrder)
            .ThenBy(candidate => candidate.Id)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.Width,
                candidate.Height,
                candidate.WalkthroughStop,
                candidate.RoomType,
                candidate.AnalysisDescription,
            })
            .ToArrayAsync(cancellationToken);
        var videos = await dbContext.PropertyVideos.AsNoTracking()
            .Where(candidate => candidate.OrganizationId == organizationId
                && candidate.PropertyId == propertyId
                && candidate.ProcessingStatus == PropertyVideoProcessingStatus.Completed
                && candidate.EnhancedDurationMs != null)
            .Select(candidate => new { candidate.Id, DurationMs = candidate.EnhancedDurationMs!.Value })
            .ToDictionaryAsync(candidate => candidate.Id, candidate => candidate.DurationMs, cancellationToken);
        return new CampaignVideoEditContext(
            photos.Select(photo => new CampaignVideoPhoto(
                photo.Id,
                photo.Width,
                photo.Height,
                Trim(photo.WalkthroughStop ?? photo.RoomType, 80),
                Trim(photo.AnalysisDescription, 240))).ToArray(),
            videos);
    }

    /// <summary>
    /// The voice the video speaks with now: the latest re-voice among the revisions it is built from, or the
    /// campaign's voice. Completed revisions are exactly the ones in the current video, since an undo marks its
    /// revision undone and a new revision always starts from the current video.
    /// </summary>
    public static CampaignVoiceAccent CurrentVoice(
        CampaignGenerationJob job,
        IEnumerable<CampaignVideoRevision> revisions)
    {
        foreach (var revision in revisions
                     .Where(candidate => candidate.Status == CampaignVideoRevisionStatus.Completed)
                     .OrderByDescending(candidate => candidate.ResultVersion))
        {
            var voice = Deserialize(revision.EditsJson).Edits
                .LastOrDefault(edit => edit.Kind == CampaignVideoEditKind.ChangeVoice && edit.VoiceAccent is not null);
            if (voice is not null)
            {
                return voice.VoiceAccent!.Value;
            }
        }

        return job.VoiceAccent;
    }

    private static string? Trim(string? value, int length)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length <= length ? normalized : normalized[..length];
    }
}
