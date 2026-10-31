using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ListingStudio.Application.Audio;
using ListingStudio.Application.Billing;
using ListingStudio.Application.Campaigns;
using ListingStudio.Application.Properties;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ListingStudio.Infrastructure.Campaigns;

public sealed partial class AgentAvatarPreviewService(
    ApplicationDbContext dbContext,
    IPropertyMediaStorage propertyMediaStorage,
    ICampaignAssetStorage campaignAssetStorage,
    IAgentAvatarPreviewRenderer renderer,
    IAgentAvatarVideoProvider presenterProvider,
    IVoiceProvider voiceProvider,
    IPlanFeatureGate planFeatureGate,
    ILogger<AgentAvatarPreviewService> logger) : IAgentAvatarPreviewService
{
    /// <summary>The line the presenter speaks in the talking preview (about four seconds).</summary>
    public const string GreetingText = "Hi there, welcome in! Let me show you around this home.";

    private const string GreetingSegmentId = "presenter-preview-greeting";
    private const int GreetingPlannedDurationMs = 4_500;

    public async Task<AgentAvatarPreviewResult?> PrepareAsync(
        string userId,
        Guid portraitId,
        CancellationToken cancellationToken = default)
    {
        var portrait = await FindOwnedAsync(userId, portraitId, cancellationToken);
        if (portrait is null)
        {
            return null;
        }

        string? fallbackReason = null;
        if (presenterProvider.IsEnabled
            && (await planFeatureGate.GetAccessAsync(portrait.OrganizationId, userId, cancellationToken)).IncludesPremiumAi)
        {
            try
            {
                await PrepareTalkingAsync(portrait, cancellationToken);
                return new AgentAvatarPreviewResult(LipSynced: true);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogTalkingPreviewFailed(exception, portrait.Id);
                fallbackReason = "The talking preview could not be made right now, so this is the silent layout preview. Try again in a few minutes.";
            }
        }

        await PrepareLayoutAsync(portrait, cancellationToken);
        return new AgentAvatarPreviewResult(LipSynced: false, fallbackReason);
    }

    private async Task PrepareTalkingAsync(AgentPortrait portrait, CancellationToken cancellationToken)
    {
        var previewPath = TalkingPreviewPath(portrait);
        if (await ExistsAsync(previewPath, cancellationToken))
        {
            return;
        }

        var voice = await voiceProvider.GenerateAsync(
            new VoiceGenerationRequest(
                [new VoiceNarrationSegment(GreetingSegmentId, GreetingText, 0, GreetingPlannedDurationMs)]),
            cancellationToken);
        if (voice.AudioData.Length == 0 || voice.DurationMs is < 500 or > 15_000)
        {
            throw new InvalidDataException("The presenter preview greeting audio is invalid.");
        }

        await using var portraitContent = await propertyMediaStorage.OpenReadAsync(portrait.BlobPath, cancellationToken)
            ?? throw new FileNotFoundException("The agent headshot could not be opened.");
        await using var audio = new MemoryStream(voice.AudioData, writable: false);
        var generated = await presenterProvider.GenerateAsync(
            new AgentAvatarVideoProviderRequest(
                portrait.OriginalFilename,
                portrait.MimeType,
                portraitContent,
                $"presenter-preview{voice.FileExtension}",
                voice.ContentType,
                audio,
                voice.DurationMs,
                TalkingFingerprint(portrait)),
            cancellationToken);
        await using var video = generated.Content;
        if (!string.Equals(generated.ContentType, "video/mp4", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The agent-presenter provider did not return an MP4 preview.");
        }

        await campaignAssetStorage.StoreAsync(previewPath, video, "video/mp4", cancellationToken);
    }

    private async Task PrepareLayoutAsync(AgentPortrait portrait, CancellationToken cancellationToken)
    {
        var previewPath = PreviewPath(portrait);
        if (await ExistsAsync(previewPath, cancellationToken))
        {
            return;
        }

        byte[] source;
        await using (var content = await propertyMediaStorage.OpenReadAsync(portrait.BlobPath, cancellationToken)
            ?? throw new FileNotFoundException("The agent headshot could not be opened."))
        using (var buffer = new MemoryStream())
        {
            await content.CopyToAsync(buffer, cancellationToken);
            source = buffer.ToArray();
        }

        var preview = await renderer.RenderAsync(source, portrait.MimeType, cancellationToken);
        await using var previewStream = new MemoryStream(preview, writable: false);
        await campaignAssetStorage.StoreAsync(previewPath, previewStream, "video/mp4", cancellationToken);
    }

    private async Task<bool> ExistsAsync(string assetPath, CancellationToken cancellationToken)
    {
        await using var cached = await campaignAssetStorage.OpenReadAsync(assetPath, cancellationToken);
        return cached is not null;
    }

    public async Task<AgentAvatarPreviewContent?> OpenReadAsync(
        string userId,
        Guid portraitId,
        CancellationToken cancellationToken = default)
    {
        var portrait = await FindOwnedAsync(userId, portraitId, cancellationToken);
        if (portrait is null)
        {
            return null;
        }

        // A talking preview, once made, replaces the layout preview for this headshot.
        var content = (presenterProvider.IsEnabled
                ? await campaignAssetStorage.OpenReadAsync(TalkingPreviewPath(portrait), cancellationToken)
                : null)
            ?? await campaignAssetStorage.OpenReadAsync(PreviewPath(portrait), cancellationToken);
        return content is null ? null : new AgentAvatarPreviewContent(content, "video/mp4");
    }

    private async Task<AgentPortrait?> FindOwnedAsync(
        string userId,
        Guid portraitId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var organizationId = await dbContext.OrganizationMembers.AsNoTracking()
            .Where(member => member.UserId == userId)
            .Select(member => (Guid?)member.OrganizationId)
            .FirstOrDefaultAsync(cancellationToken);
        if (organizationId is null)
        {
            return null;
        }

        return await dbContext.AgentPortraits.AsNoTracking().SingleOrDefaultAsync(
            portrait => portrait.Id == portraitId
                && portrait.OrganizationId == organizationId
                && portrait.UserId == userId,
            cancellationToken);
    }

    private static string PreviewPath(AgentPortrait portrait) =>
        $"organizations/{portrait.OrganizationId:N}/agent-avatar-previews/{portrait.Id:N}.mp4";

    // Keyed by the headshot version, the presenter and voice providers and the greeting, so a change to any of
    // them makes (and pays for) one new clip instead of serving a stale one.
    private string TalkingPreviewPath(AgentPortrait portrait) =>
        $"organizations/{portrait.OrganizationId:N}/agent-avatar-previews/{portrait.Id:N}-talking-{TalkingFingerprint(portrait)[..24]}.mp4";

    private string TalkingFingerprint(AgentPortrait portrait) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Join(
            '\n',
            portrait.Id.ToString("N"),
            portrait.UpdatedAtUtc.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            presenterProvider.GenerationVersion,
            voiceProvider.GenerationVersion,
            GreetingText)))).ToLowerInvariant();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Talking presenter preview failed for portrait {PortraitId}; using the layout preview")]
    private partial void LogTalkingPreviewFailed(Exception exception, Guid portraitId);
}
