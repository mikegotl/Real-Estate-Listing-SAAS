using ListingStudio.Application.Administration;
using ListingStudio.Application.Help;
using ListingStudio.Application.Properties;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Help;
using ListingStudio.Infrastructure.Campaigns;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ListingStudio.Infrastructure.Help;

public sealed partial class HelpChatService(
    ApplicationDbContext dbContext,
    IHelpChatAssistant assistant,
    IPropertyPhotoFixService photoFixService,
    IAiUsagePropertyContext aiUsagePropertyContext,
    TimeProvider timeProvider,
    ILogger<HelpChatService> logger) : IHelpChatService
{
    /// <summary>The assistant sees this many of the latest messages, which keeps each call small.</summary>
    private const int ConversationTurnsSent = 20;

    public async Task<HelpChat> GetAsync(string userId, CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var latest = await dbContext.HelpChatMessages.AsNoTracking()
            .Where(message => message.OrganizationId == organizationId && message.UserId == userId)
            .OrderByDescending(message => message.CreatedAtUtc)
            .Select(message => new { message.ConversationId, message.CreatedAtUtc })
            .FirstOrDefaultAsync(cancellationToken);
        var conversationId = latest is not null
            && timeProvider.GetUtcNow() - latest.CreatedAtUtc < IHelpChatService.ConversationIdleTimeout
                ? latest.ConversationId
                : (Guid?)null;
        return await ToChatAsync(organizationId, userId, conversationId, cancellationToken);
    }

    public async Task<HelpChat> SendAsync(
        string userId,
        Guid? conversationId,
        string text,
        string? pagePath,
        Guid? propertyId,
        Guid? selectedMediaId,
        CancellationToken cancellationToken = default)
    {
        var normalized = text?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            throw new InvalidOperationException("Type a question first.");
        }

        if (normalized.Length > IHelpChatService.MaximumMessageLength)
        {
            throw new InvalidOperationException($"Keep the message under {IHelpChatService.MaximumMessageLength} characters.");
        }

        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        if (await MessagesLeftTodayAsync(organizationId, userId, cancellationToken) <= 0)
        {
            throw new InvalidOperationException(
                $"You've asked {IHelpChatService.MaximumMessagesPerDay} questions today, the daily limit. The assistant will be back tomorrow; the Comments button still reaches the Listing Studio team.");
        }

        // Only a property in the user's own workspace gives the assistant context; any other id is ignored.
        var property = propertyId is { } id
            ? await dbContext.Properties.AsNoTracking()
                .Where(candidate => candidate.Id == id && candidate.OrganizationId == organizationId)
                .Select(candidate => new { candidate.Id, candidate.Address1, candidate.City, candidate.State })
                .SingleOrDefaultAsync(cancellationToken)
            : null;

        var conversation = conversationId ?? Guid.NewGuid();
        var selectedPhoto = property is not null && selectedMediaId is { } mediaId
            ? await dbContext.PropertyMedia.AsNoTracking()
                .Where(media => media.Id == mediaId
                    && media.PropertyId == property.Id
                    && media.OrganizationId == organizationId)
                .Select(media => new { media.OriginalFilename, media.WalkthroughStop })
                .SingleOrDefaultAsync(cancellationToken)
            : null;
        var history = conversationId is null
            ? []
            : await dbContext.HelpChatMessages.AsNoTracking()
                .Where(message => message.OrganizationId == organizationId
                    && message.UserId == userId
                    && message.ConversationId == conversation)
                .OrderByDescending(message => message.CreatedAtUtc)
                .Take(ConversationTurnsSent - 1)
                .ToListAsync(cancellationToken);
        var request = new HelpChatRequest(
            pagePath,
            property is null
                ? null
                : await SummarizeAsync(organizationId, property.Id, $"{property.Address1}, {property.City}, {property.State}", cancellationToken),
            selectedPhoto is null
                ? null
                : new HelpChatPhotoSummary(
                    selectedPhoto.OriginalFilename,
                    selectedPhoto.WalkthroughStop,
                    photoFixService.IsClutterRemovalEnabled),
            history
                .OrderBy(message => message.CreatedAtUtc)
                .Select(message => new HelpChatTurn(message.Role, message.Text))
                .Append(new HelpChatTurn(HelpChatRole.User, normalized))
                .ToArray());

        HelpChatAssistantReply reply;
        try
        {
            using (property is null ? null : aiUsagePropertyContext.BeginProperty(property.Id))
            {
                reply = await assistant.ReplyAsync(request, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException
            or InvalidDataException
            or InvalidOperationException
            or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            LogAssistantFailed(exception, organizationId);
            throw new InvalidOperationException("The assistant could not answer just now. Please try again.");
        }

        var now = timeProvider.GetUtcNow();
        var question = HelpChatMessage.FromUser(organizationId, userId, conversation, normalized, pagePath, property?.Id, now);
        dbContext.HelpChatMessages.Add(question);
        dbContext.HelpChatMessages.Add(HelpChatMessage.FromAssistant(question, reply.Text, now.AddMilliseconds(1)));
        await dbContext.SaveChangesAsync(cancellationToken);
        return (await ToChatAsync(organizationId, userId, conversation, cancellationToken)) with
        {
            ProposedPhotoAction = reply.PhotoAction,
        };
    }

    public async Task<IReadOnlyList<HelpChatVideo>> GetVideosAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var videos = await FinishedVideosAsync(organizationId, propertyId, cancellationToken);
        return videos
            .Select(video => new HelpChatVideo(video.JobId, video.Kind, CampaignVideoChatService.OutputName(video.Kind)))
            .ToArray();
    }

    private async Task<HelpChatPropertySummary> SummarizeAsync(
        Guid organizationId,
        Guid propertyId,
        string address,
        CancellationToken cancellationToken)
    {
        var photos = await dbContext.PropertyMedia.AsNoTracking()
            .Where(media => media.OrganizationId == organizationId && media.PropertyId == propertyId)
            .Select(media => new { media.IsInGallery, media.WalkthroughStop })
            .ToListAsync(cancellationToken);
        var latest = await dbContext.CampaignGenerationJobs.AsNoTracking()
            .Where(job => job.OrganizationId == organizationId && job.PropertyId == propertyId)
            .OrderByDescending(job => job.CreatedAtUtc)
            .Select(job => new { job.Status, job.CurrentStage, job.LastError })
            .FirstOrDefaultAsync(cancellationToken);
        var videos = await FinishedVideosAsync(organizationId, propertyId, cancellationToken);
        return new HelpChatPropertySummary(
            address,
            photos.Count(photo => !photo.IsInGallery && photo.WalkthroughStop is not null),
            photos.Count(photo => photo.IsInGallery),
            latest is null
                ? null
                : latest.Status is CampaignGenerationStatus.Queued or CampaignGenerationStatus.Running
                    ? $"{latest.Status} (step: {latest.CurrentStage})"
                    : latest.Status.ToString(),
            latest?.Status == CampaignGenerationStatus.Failed ? latest.LastError : null,
            videos.Select(video => CampaignVideoChatService.OutputName(video.Kind)).ToArray());
    }

    private async Task<IReadOnlyList<(Guid JobId, CampaignOutputKind Kind)>> FinishedVideosAsync(
        Guid organizationId,
        Guid propertyId,
        CancellationToken cancellationToken)
    {
        var jobId = await dbContext.CampaignGenerationJobs.AsNoTracking()
            .Where(job => job.OrganizationId == organizationId
                && job.PropertyId == propertyId
                && job.Status == CampaignGenerationStatus.Completed)
            .OrderByDescending(job => job.CreatedAtUtc)
            .Select(job => (Guid?)job.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (jobId is null)
        {
            return [];
        }

        var kinds = await dbContext.CampaignDeliverables.AsNoTracking()
            .Where(deliverable => deliverable.OrganizationId == organizationId
                && deliverable.CampaignGenerationJobId == jobId
                && deliverable.Status == CampaignDeliverableStatus.Rendered)
            .Select(deliverable => deliverable.Kind)
            .ToListAsync(cancellationToken);
        return kinds.Order().Select(kind => (jobId.Value, kind)).ToArray();
    }

    private async Task<HelpChat> ToChatAsync(
        Guid organizationId,
        string userId,
        Guid? conversationId,
        CancellationToken cancellationToken)
    {
        var messages = conversationId is null
            ? []
            : await dbContext.HelpChatMessages.AsNoTracking()
                .Where(message => message.OrganizationId == organizationId
                    && message.UserId == userId
                    && message.ConversationId == conversationId)
                .OrderBy(message => message.CreatedAtUtc)
                .ToListAsync(cancellationToken);
        return new HelpChat(
            conversationId,
            await MessagesLeftTodayAsync(organizationId, userId, cancellationToken),
            messages.Select(message => new HelpChatEntry(message.Id, message.Role, message.Text, message.CreatedAtUtc)).ToArray());
    }

    private async Task<int> MessagesLeftTodayAsync(Guid organizationId, string userId, CancellationToken cancellationToken)
    {
        var since = timeProvider.GetUtcNow().AddDays(-1);
        var sent = await dbContext.HelpChatMessages.AsNoTracking()
            .CountAsync(message => message.OrganizationId == organizationId
                && message.UserId == userId
                && message.Role == HelpChatRole.User
                && message.CreatedAtUtc >= since,
                cancellationToken);
        return Math.Max(0, IHelpChatService.MaximumMessagesPerDay - sent);
    }

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "The help chat assistant failed for organization {OrganizationId}")]
    private partial void LogAssistantFailed(Exception exception, Guid organizationId);
}
