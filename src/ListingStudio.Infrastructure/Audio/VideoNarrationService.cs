using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ListingStudio.Application.Audio;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Videos;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ListingStudio.Infrastructure.Audio;

public sealed class VideoNarrationService(
    ApplicationDbContext dbContext,
    IVoiceProvider voiceProvider,
    ICampaignAssetStorage storage) : IVideoNarrationService
{
    private const int MaximumAudioBytes = 50 * 1024 * 1024;
    private const int TimingToleranceMs = 250;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<VideoNarrationResult?> GetLatestAsync(
        string userId,
        Guid videoProductionPlanId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var narration = await dbContext.VideoNarrations
            .AsNoTracking()
            .Where(candidate => candidate.OrganizationId == organizationId
                && candidate.VideoProductionPlanId == videoProductionPlanId)
            .OrderByDescending(candidate => candidate.Version)
            .FirstOrDefaultAsync(cancellationToken);
        return narration is null ? null : ToResult(narration, reused: false);
    }

    public async Task<VideoNarrationResult?> GenerateAsync(
        string userId,
        Guid videoProductionPlanId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var plan = await dbContext.VideoProductionPlans
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == videoProductionPlanId
                    && candidate.OrganizationId == organizationId,
                cancellationToken);
        if (plan is null)
        {
            return null;
        }

        var specification = JsonSerializer.Deserialize<VideoProductionSpecification>(
            plan.SpecificationJson,
            VideoSpecificationJson.Options)
            ?? throw new InvalidDataException("Stored video production specification is invalid.");
        var request = CreateRequest(specification);
        var fingerprint = CreateFingerprint(plan.Id, voiceProvider.GenerationVersion, request);
        var existing = await FindByFingerprintAsync(organizationId, plan.Id, fingerprint, cancellationToken);
        if (existing is not null)
        {
            return ToResult(existing, reused: true);
        }

        var generated = await voiceProvider.GenerateAsync(request, cancellationToken);
        ValidateGeneratedResult(request, generated, (int)specification.RequestedDuration * 1_000);
        var timingJson = generated.Timing is null
            ? null
            : JsonSerializer.Serialize(generated.Timing, SerializerOptions);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        existing = await FindByFingerprintAsync(organizationId, plan.Id, fingerprint, cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return ToResult(existing, reused: true);
        }

        var latestVersion = await dbContext.VideoNarrations
            .Where(candidate => candidate.OrganizationId == organizationId
                && candidate.VideoProductionPlanId == plan.Id)
            .Select(candidate => (int?)candidate.Version)
            .MaxAsync(cancellationToken) ?? 0;
        var narrationId = Guid.NewGuid();
        var assetPath = BuildAssetPath(
            organizationId,
            plan.PropertyId,
            plan.Id,
            narrationId,
            generated.FileExtension);
        var narration = VideoNarration.Create(
            narrationId,
            organizationId,
            plan.PropertyId,
            plan.Id,
            latestVersion + 1,
            voiceProvider.GenerationVersion,
            fingerprint,
            assetPath,
            generated.ContentType,
            generated.DurationMs,
            timingJson);

        var stored = false;
        try
        {
            await using var audio = new MemoryStream(generated.AudioData, writable: false);
            await storage.StoreAsync(assetPath, audio, generated.ContentType, cancellationToken);
            stored = true;
            dbContext.VideoNarrations.Add(narration);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ToResult(narration, reused: false);
        }
        catch (Exception exception)
        {
            if (stored)
            {
                try
                {
                    await storage.DeleteAsync(assetPath, CancellationToken.None);
                }
                catch (Exception cleanupException)
                {
                    throw new AggregateException(
                        "Narration persistence failed and its stored asset could not be cleaned up.",
                        exception,
                        cleanupException);
                }
            }

            throw;
        }
    }

    public async Task<CampaignAssetContent?> OpenAudioAsync(
        string userId,
        Guid narrationId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var narration = await dbContext.VideoNarrations
            .AsNoTracking()
            .Where(candidate => candidate.Id == narrationId && candidate.OrganizationId == organizationId)
            .Select(candidate => new { candidate.AssetPath, candidate.ContentType })
            .SingleOrDefaultAsync(cancellationToken);
        if (narration is null)
        {
            return null;
        }

        var content = await storage.OpenReadAsync(narration.AssetPath, cancellationToken);
        return content is null ? null : new CampaignAssetContent(content, narration.ContentType);
    }

    private static VoiceGenerationRequest CreateRequest(VideoProductionSpecification specification)
    {
        var segments = specification.Audio.NarrationSegments
            .OrderBy(segment => segment.StartMs)
            .Select(segment => new VoiceNarrationSegment(
                segment.Id,
                segment.Text,
                segment.StartMs,
                segment.DurationMs))
            .ToArray();
        if (segments.Length == 0)
        {
            throw new InvalidOperationException("The production specification contains no narration segments.");
        }

        return new VoiceGenerationRequest(segments);
    }

    private static void ValidateGeneratedResult(
        VoiceGenerationRequest request,
        VoiceGenerationResult result,
        int programDurationMs)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.AudioData.Length is 0 or > MaximumAudioBytes)
        {
            throw new InvalidDataException("Voice provider returned an empty or oversized audio file.");
        }

        if (result.DurationMs <= 0)
        {
            throw new InvalidDataException("Voice provider returned an invalid audio duration.");
        }

        if (!result.ContentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Voice provider returned an unsupported content type.");
        }

        if (result.FileExtension.Length is < 2 or > 10 || result.FileExtension[0] != '.'
            || result.FileExtension.Skip(1).Any(character => !char.IsLetterOrDigit(character)))
        {
            throw new InvalidDataException("Voice provider returned an invalid file extension.");
        }

        if (result.Timing is null)
        {
            return;
        }

        var requestedIds = request.Segments.Select(segment => segment.Id).Order(StringComparer.Ordinal).ToArray();
        var returnedIds = result.Timing.Segments.Select(segment => segment.SegmentId).Order(StringComparer.Ordinal).ToArray();
        if (!requestedIds.SequenceEqual(returnedIds, StringComparer.Ordinal))
        {
            throw new InvalidDataException("Voice provider timing does not match the requested narration segments.");
        }

        // The renderer starts each segment at its planned time, or right after the previous one when that
        // ran long, so a segment may overrun its own slot as long as the whole narration ends in the program.
        var timingById = result.Timing.Segments.ToDictionary(segment => segment.SegmentId, StringComparer.Ordinal);
        var placedEndMs = 0;
        foreach (var segment in request.Segments.OrderBy(segment => segment.PlannedStartMs))
        {
            var measured = timingById[segment.Id];
            if (measured.StartMs < 0 || measured.EndMs <= measured.StartMs || measured.EndMs > result.DurationMs)
            {
                throw new InvalidDataException($"Voice provider returned invalid timing for segment {segment.Id}.");
            }

            placedEndMs = Math.Max(segment.PlannedStartMs, placedEndMs) + measured.EndMs - measured.StartMs;
            if (placedEndMs > programDurationMs + TimingToleranceMs)
            {
                throw new InvalidDataException(
                    $"Generated narration segment {segment.Id} runs past the end of the video.");
            }
        }

        if (result.Timing.Characters.Any(mark => mark.StartMs < 0
            || mark.EndMs < mark.StartMs
            || mark.EndMs > result.DurationMs))
        {
            throw new InvalidDataException("Voice provider returned invalid character timing.");
        }
    }

    private Task<VideoNarration?> FindByFingerprintAsync(
        Guid organizationId,
        Guid videoProductionPlanId,
        string fingerprint,
        CancellationToken cancellationToken) => dbContext.VideoNarrations
        .AsNoTracking()
        .SingleOrDefaultAsync(
            narration => narration.OrganizationId == organizationId
                && narration.VideoProductionPlanId == videoProductionPlanId
                && narration.SourceFingerprint == fingerprint,
            cancellationToken);

    private async Task<Guid> GetOrganizationIdAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var organizationId = await dbContext.OrganizationMembers
            .AsNoTracking()
            .Where(member => member.UserId == userId)
            .Select(member => (Guid?)member.OrganizationId)
            .FirstOrDefaultAsync(cancellationToken);
        return organizationId ?? throw new UnauthorizedAccessException("The user does not belong to an organization.");
    }

    private static string CreateFingerprint(
        Guid planId,
        string generationVersion,
        VoiceGenerationRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generationVersion);
        var source = JsonSerializer.Serialize(new { planId, generationVersion, request }, SerializerOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
    }

    private static string BuildAssetPath(
        Guid organizationId,
        Guid propertyId,
        Guid planId,
        Guid narrationId,
        string extension) =>
        $"organizations/{organizationId:N}/properties/{propertyId:N}/video-plans/{planId:N}/narration/{narrationId:N}{extension.ToLowerInvariant()}";

    private static VideoNarrationResult ToResult(VideoNarration narration, bool reused)
    {
        var timing = narration.TimingJson is null
            ? null
            : JsonSerializer.Deserialize<VoiceTimingMetadata>(narration.TimingJson, SerializerOptions)
                ?? throw new InvalidDataException("Stored narration timing is invalid.");
        return new VideoNarrationResult(
            narration.Id,
            narration.VideoProductionPlanId,
            narration.Version,
            narration.GenerationVersion,
            narration.ContentType,
            narration.DurationMs,
            timing,
            narration.CreatedAtUtc,
            reused);
    }
}
