using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ListingStudio.Application.Administration;
using ListingStudio.Application.Audio;
using ListingStudio.Application.Properties;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Videos;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ListingStudio.Infrastructure.Videos;

public sealed class AgentAvatarVideoService(
    ApplicationDbContext dbContext,
    IPropertyMediaStorage propertyMediaStorage,
    ICampaignAssetStorage campaignAssetStorage,
    IAgentAvatarVideoProvider provider,
    IAgentAvatarNarrationExcerptRenderer excerptRenderer,
    IAiUsagePropertyContext aiUsagePropertyContext,
    TimeProvider timeProvider) : IAgentAvatarVideoService
{
    private static readonly JsonSerializerOptions AudioJsonOptions = new(JsonSerializerDefaults.Web);

    public bool IsEnabled => provider.IsEnabled;

    public async Task<AgentAvatarVideoResult?> GetOrCreateAsync(
        Guid organizationId,
        Guid propertyId,
        string userId,
        Guid portraitId,
        Guid narrationId,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return null;
        }

        if (organizationId == Guid.Empty || propertyId == Guid.Empty || portraitId == Guid.Empty
            || narrationId == Guid.Empty || string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("Organization, property, user, portrait, and narration identities are required.");
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var narration = await dbContext.VideoNarrations.FromSqlInterpolated($"""
            SELECT *
            FROM "VideoNarrations"
            WHERE "Id" = {narrationId}
              AND "PropertyId" = {propertyId}
              AND "OrganizationId" = {organizationId}
            FOR UPDATE
            """).AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var portrait = await dbContext.AgentPortraits.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.Id == portraitId
                && candidate.OrganizationId == organizationId
                && candidate.UserId == userId,
            cancellationToken);
        if (narration is null || portrait is null || string.IsNullOrWhiteSpace(narration.TimingJson))
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var planJson = await dbContext.VideoProductionPlans.AsNoTracking()
            .Where(candidate => candidate.Id == narration.VideoProductionPlanId
                && candidate.OrganizationId == organizationId
                && candidate.PropertyId == propertyId)
            .Select(candidate => candidate.SpecificationJson)
            .SingleOrDefaultAsync(cancellationToken);
        if (planJson is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var specification = JsonSerializer.Deserialize<VideoProductionSpecification>(
            planJson,
            VideoSpecificationJson.Options)
            ?? throw new InvalidDataException("The presenter narration plan is invalid.");
        var timing = JsonSerializer.Deserialize<ListingStudio.Application.Audio.VoiceTimingMetadata>(
            narration.TimingJson,
            AudioJsonOptions)
            ?? throw new InvalidDataException("The presenter narration timing is invalid.");
        var selection = SelectExcerpts(specification, timing);
        if (selection is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var fingerprint = Fingerprint(
            portrait.Id,
            portrait.UpdatedAtUtc,
            narration.Id,
            narration.SourceFingerprint,
            provider.GenerationVersion,
            selection);
        var existing = await dbContext.AgentAvatarVideos.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.OrganizationId == organizationId
                && candidate.PropertyId == propertyId
                && candidate.SourceFingerprint == fingerprint,
            cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return ToResult(existing, reused: true);
        }

        await using var portraitContent = await propertyMediaStorage.OpenReadAsync(portrait.BlobPath, cancellationToken)
            ?? throw new FileNotFoundException("The approved agent headshot could not be opened.");
        await using var narrationContent = await campaignAssetStorage.OpenReadAsync(narration.AssetPath, cancellationToken)
            ?? throw new FileNotFoundException("The campaign narration could not be opened.");
        var excerpt = await excerptRenderer.RenderAsync(
            narrationContent,
            narration.ContentType,
            selection.Parts,
            cancellationToken);
        await using var excerptContent = new MemoryStream(excerpt.AudioData, writable: false);
        AgentAvatarVideoProviderResult generated;
        using (aiUsagePropertyContext.BeginProperty(propertyId))
        {
            generated = await provider.GenerateAsync(
                new AgentAvatarVideoProviderRequest(
                    portrait.OriginalFilename,
                    portrait.MimeType,
                    portraitContent,
                    $"presenter{excerpt.FileExtension}",
                    excerpt.ContentType,
                    excerptContent,
                    excerpt.DurationMs,
                    fingerprint),
                cancellationToken);
        }

        await using var video = generated.Content;
        ValidateResult(generated, excerpt.DurationMs);
        var assetPath = $"organizations/{organizationId:N}/properties/{propertyId:N}/agent-avatar/{fingerprint}.mp4";
        var stored = false;
        try
        {
            var measured = new CountingReadStream(video);
            await campaignAssetStorage.StoreAsync(assetPath, measured, generated.ContentType, cancellationToken);
            stored = true;
            var metadataJson = JsonSerializer.Serialize(
                generated.Metadata.OrderBy(item => item.Key, StringComparer.Ordinal)
                    .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal));
            var entity = AgentAvatarVideo.Create(
                organizationId,
                propertyId,
                portraitId,
                narrationId,
                fingerprint,
                selection.IntroTimelineStartMs,
                selection.IntroDurationMs,
                selection.OutroTimelineStartMs,
                selection.OutroDurationMs,
                generated.Width,
                generated.Height,
                generated.Provider,
                generated.Model,
                provider.GenerationVersion,
                generated.ProviderRequestId,
                metadataJson,
                generated.EstimatedCostUsd,
                assetPath,
                generated.ContentType,
                measured.BytesRead,
                timeProvider.GetUtcNow());
            dbContext.AgentAvatarVideos.Add(entity);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ToResult(entity, reused: false);
        }
        catch
        {
            if (stored)
            {
                await campaignAssetStorage.DeleteAsync(assetPath, CancellationToken.None);
            }

            throw;
        }
    }

    private ExcerptSelection? SelectExcerpts(
        VideoProductionSpecification specification,
        ListingStudio.Application.Audio.VoiceTimingMetadata timing)
    {
        var segments = specification.Audio.NarrationSegments.OrderBy(segment => segment.StartMs).ToArray();
        if (segments.Length == 0)
        {
            return null;
        }

        var timingById = timing.Segments.ToDictionary(segment => segment.SegmentId, StringComparer.Ordinal);
        if (segments.Any(segment => !timingById.ContainsKey(segment.Id)))
        {
            return null;
        }

        var placement = new Dictionary<string, (int StartMs, int EndMs)>(StringComparer.Ordinal);
        var previousEnd = 0;
        foreach (var segment in segments)
        {
            var measured = timingById[segment.Id];
            if (measured.StartMs < 0 || measured.EndMs <= measured.StartMs)
            {
                return null;
            }

            var start = Math.Max(segment.StartMs, previousEnd);
            var end = start + measured.EndMs - measured.StartMs;
            placement[segment.Id] = (start, end);
            previousEnd = end;
        }

        var first = segments[0];
        var firstTiming = timingById[first.Id];
        var firstPlacement = placement[first.Id];
        var introDuration = Math.Min(provider.MaximumIntroDurationMs, firstTiming.EndMs - firstTiming.StartMs);
        if (introDuration < 500)
        {
            return null;
        }

        var parts = new List<AgentAvatarNarrationExcerptPart>
        {
            new(firstTiming.StartMs, introDuration),
        };
        var outroTimelineStart = 0;
        var outroDuration = 0;
        var last = segments[^1];
        var lastTiming = timingById[last.Id];
        var lastPlacement = placement[last.Id];
        var availableOutro = first.Id == last.Id
            ? Math.Max(0, lastTiming.EndMs - lastTiming.StartMs - introDuration)
            : lastTiming.EndMs - lastTiming.StartMs;
        outroDuration = Math.Min(provider.MaximumOutroDurationMs, availableOutro);
        if (outroDuration >= 500)
        {
            var rawStart = lastTiming.EndMs - outroDuration;
            outroTimelineStart = lastPlacement.EndMs - outroDuration;
            parts.Add(new AgentAvatarNarrationExcerptPart(rawStart, outroDuration));
        }
        else
        {
            outroDuration = 0;
        }

        return new ExcerptSelection(
            firstPlacement.StartMs,
            introDuration,
            outroTimelineStart,
            outroDuration,
            parts);
    }

    private static void ValidateResult(AgentAvatarVideoProviderResult result, int requestedDurationMs)
    {
        if (!string.Equals(result.ContentType, "video/mp4", StringComparison.OrdinalIgnoreCase)
            || result.Width <= 0 || result.Height <= 0
            || result.DurationMs < requestedDurationMs - 500
            || result.DurationMs > requestedDurationMs + 3_000
            || string.IsNullOrWhiteSpace(result.Provider)
            || string.IsNullOrWhiteSpace(result.Model)
            || result.EstimatedCostUsd < 0
            || result.Metadata.Count > 50
            || result.Metadata.Any(item => item.Key.Length > 100 || item.Value.Length > 500))
        {
            throw new InvalidDataException("The agent-presenter provider returned invalid video metadata.");
        }
    }

    private static string Fingerprint(
        Guid portraitId,
        DateTimeOffset portraitUpdatedAtUtc,
        Guid narrationId,
        string narrationFingerprint,
        string generationVersion,
        ExcerptSelection selection)
    {
        var json = JsonSerializer.Serialize(new
        {
            portraitId,
            portraitUpdatedAtUtc,
            narrationId,
            narrationFingerprint,
            generationVersion,
            selection.IntroTimelineStartMs,
            selection.IntroDurationMs,
            selection.OutroTimelineStartMs,
            selection.OutroDurationMs,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    private static AgentAvatarVideoResult ToResult(AgentAvatarVideo video, bool reused) => new(
        video.Id,
        video.VideoNarrationId,
        video.IntroTimelineStartMs,
        video.IntroDurationMs,
        video.OutroTimelineStartMs,
        video.OutroDurationMs,
        video.Width,
        video.Height,
        video.Provider,
        video.Model,
        video.EstimatedCostUsd,
        reused);

    private sealed record ExcerptSelection(
        int IntroTimelineStartMs,
        int IntroDurationMs,
        int OutroTimelineStartMs,
        int OutroDurationMs,
        IReadOnlyList<AgentAvatarNarrationExcerptPart> Parts);

    private sealed class CountingReadStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            BytesRead += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
