using System.Text.Json;
using ListingStudio.Application.Administration;
using ListingStudio.Application.Campaigns;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Videos;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ListingStudio.Infrastructure.Campaigns;

public sealed partial class CampaignVideoChatService(
    ApplicationDbContext dbContext,
    ICampaignVideoChatAssistant assistant,
    IAiUsagePropertyContext aiUsagePropertyContext,
    TimeProvider timeProvider,
    ILogger<CampaignVideoChatService> logger) : ICampaignVideoChatService
{
    /// <summary>Each message costs an AI call, so one video's chat stops after this many of the user's messages.</summary>
    public const int MaximumUserMessagesPerVideo = 60;

    public Task<CampaignVideoChat?> GetAsync(
        string userId,
        Guid jobId,
        CampaignOutputKind kind,
        CancellationToken cancellationToken = default) =>
        GetCoreAsync(tracked => FindAsync(userId, jobId, kind, tracked, cancellationToken), cancellationToken);

    public Task<CampaignVideoChat?> GetForVideoAsync(
        string userId,
        Guid deliverableId,
        CancellationToken cancellationToken = default) =>
        GetCoreAsync(tracked => FindByIdAsync(userId, deliverableId, tracked, cancellationToken), cancellationToken);

    public Task<CampaignVideoChat?> SendAsync(
        string userId,
        Guid jobId,
        CampaignOutputKind kind,
        string text,
        CancellationToken cancellationToken = default) =>
        SendCoreAsync(tracked => FindAsync(userId, jobId, kind, tracked, cancellationToken), userId, text, cancellationToken);

    public Task<CampaignVideoChat?> SendForVideoAsync(
        string userId,
        Guid deliverableId,
        string text,
        CancellationToken cancellationToken = default) =>
        SendCoreAsync(tracked => FindByIdAsync(userId, deliverableId, tracked, cancellationToken), userId, text, cancellationToken);

    public Task<CampaignVideoChat?> RenderAsync(
        string userId,
        Guid jobId,
        CampaignOutputKind kind,
        Guid messageId,
        CancellationToken cancellationToken = default) =>
        RenderCoreAsync(tracked => FindAsync(userId, jobId, kind, tracked, cancellationToken), userId, messageId, cancellationToken);

    public Task<CampaignVideoChat?> RenderForVideoAsync(
        string userId,
        Guid deliverableId,
        Guid messageId,
        CancellationToken cancellationToken = default) =>
        RenderCoreAsync(tracked => FindByIdAsync(userId, deliverableId, tracked, cancellationToken), userId, messageId, cancellationToken);

    public Task<CampaignVideoChat?> ConfirmFixAsync(
        string userId,
        Guid jobId,
        CampaignOutputKind kind,
        Guid messageId,
        bool worked,
        string? feedback,
        CancellationToken cancellationToken = default) =>
        ConfirmFixCoreAsync(tracked => FindAsync(userId, jobId, kind, tracked, cancellationToken), messageId, worked, feedback, cancellationToken);

    public Task<CampaignVideoChat?> ConfirmFixForVideoAsync(
        string userId,
        Guid deliverableId,
        Guid messageId,
        bool worked,
        string? feedback,
        CancellationToken cancellationToken = default) =>
        ConfirmFixCoreAsync(tracked => FindByIdAsync(userId, deliverableId, tracked, cancellationToken), messageId, worked, feedback, cancellationToken);

    public Task<CampaignVideoChat?> UndoAsync(
        string userId,
        Guid jobId,
        CampaignOutputKind kind,
        CancellationToken cancellationToken = default) =>
        UndoCoreAsync(tracked => FindAsync(userId, jobId, kind, tracked, cancellationToken), cancellationToken);

    public Task<CampaignVideoChat?> UndoForVideoAsync(
        string userId,
        Guid deliverableId,
        CancellationToken cancellationToken = default) =>
        UndoCoreAsync(tracked => FindByIdAsync(userId, deliverableId, tracked, cancellationToken), cancellationToken);

    private async Task<CampaignVideoChat?> GetCoreAsync(
        Func<bool, Task<CampaignDeliverable?>> find,
        CancellationToken cancellationToken)
    {
        var deliverable = await find(false);
        return deliverable is null ? null : await ToChatAsync(deliverable, cancellationToken);
    }

    private async Task<CampaignVideoChat?> SendCoreAsync(
        Func<bool, Task<CampaignDeliverable?>> find,
        string userId,
        string text,
        CancellationToken cancellationToken)
    {
        var normalized = text?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            throw new InvalidOperationException("Type a question about this video or what you would like changed.");
        }

        if (normalized.Length > ICampaignVideoChatService.MaximumMessageLength)
        {
            throw new InvalidOperationException($"Keep the message under {ICampaignVideoChatService.MaximumMessageLength} characters.");
        }

        var deliverable = await find(false);
        if (deliverable is null)
        {
            return null;
        }

        var kind = deliverable.Kind;
        var history = await dbContext.CampaignVideoChatMessages.AsNoTracking()
            .Where(message => message.OrganizationId == deliverable.OrganizationId
                && message.CampaignDeliverableId == deliverable.Id)
            .OrderBy(message => message.CreatedAtUtc)
            .ThenBy(message => message.Id)
            .ToListAsync(cancellationToken);
        if (history.Count(message => message.Role == CampaignVideoChatRole.User) >= MaximumUserMessagesPerVideo)
        {
            throw new InvalidOperationException(
                "This video's chat has reached its message limit. Generate an updated campaign to start a new chat.");
        }

        var job = await dbContext.CampaignGenerationJobs.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == deliverable.CampaignGenerationJobId, cancellationToken);
        var revisions = await RevisionsAsync(deliverable, cancellationToken);
        var specification = JsonSerializer.Deserialize<VideoProductionSpecification>(
            deliverable.SpecificationJson,
            VideoSpecificationJson.Options)
            ?? throw new InvalidDataException("The stored video plan is invalid.");
        var context = await CampaignVideoEditing.LoadContextAsync(
            dbContext,
            deliverable.OrganizationId,
            deliverable.PropertyId,
            cancellationToken);
        var hasVoiceover = specification.Audio.NarrationSegments.Count > 0;
        CampaignVoiceAccent? currentVoice = hasVoiceover ? CampaignVideoEditing.CurrentVoice(job, revisions) : null;
        var request = new CampaignVideoChatRequest(
            await VideoNameAsync(deliverable, cancellationToken),
            (int)specification.RequestedDuration,
            specification.AspectRatio == VideoAspectRatio.Landscape16By9 ? "16:9" : "9:16",
            CampaignVideoEditor.Describe(specification, context.Photos),
            context.Photos,
            specification.FactBindings
                .Where(binding => !string.IsNullOrWhiteSpace(binding.Value) && binding.Value.Length <= 120)
                .Select(binding => new CampaignVideoFact(binding.Key, binding.Value))
                .ToArray(),
            CampaignVideoEditor.TrackOf(specification.Audio.Music),
            specification.Audio.Music.GainDb,
            hasVoiceover,
            currentVoice,
            CampaignVideoEditor.CaptionStyleOf(specification),
            RendersLeft(revisions),
            history
                .Select(message => new CampaignVideoChatTurn(message.Role, message.Text))
                .Append(new CampaignVideoChatTurn(CampaignVideoChatRole.User, normalized))
                .ToArray(),
            CampaignVideoProductionNotes.Describe(kind, specification, job, currentVoice));

        CampaignVideoChatReply reply;
        try
        {
            using (aiUsagePropertyContext.BeginProperty(deliverable.PropertyId))
            {
                reply = await assistant.ReplyAsync(request, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException
            or InvalidDataException
            or InvalidOperationException
            or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            LogAssistantFailed(exception, deliverable.Id);
            throw new InvalidOperationException("The video assistant could not answer just now. Please try again.");
        }

        var result = CampaignVideoEditor.Apply(specification, reply.Edits, context);
        var answer = reply.Reply;
        if (result.Rejected.Count > 0)
        {
            answer += " " + (result.Applied.Count > 0 ? "I couldn't do all of it: " : "I couldn't make that change: ")
                + string.Join(" ", result.Rejected.Select(outcome => outcome.Description).Distinct());
        }

        var now = timeProvider.GetUtcNow();
        dbContext.CampaignVideoChatMessages.Add(CampaignVideoChatMessage.FromUser(
            deliverable,
            userId,
            normalized,
            now));
        dbContext.CampaignVideoChatMessages.Add(CampaignVideoChatMessage.FromAssistant(
            deliverable,
            answer,
            result.Applied.Count == 0
                ? null
                : CampaignVideoEditing.Serialize(new CampaignVideoProposal(
                    result.Applied.Select(outcome => outcome.Edit).ToArray(),
                    result.Applied.Select(outcome => outcome.Description).ToArray())),
            now.AddMilliseconds(1)));
        await dbContext.SaveChangesAsync(cancellationToken);
        return await ToChatAsync(deliverable, cancellationToken);
    }

    private async Task<CampaignVideoChat?> RenderCoreAsync(
        Func<bool, Task<CampaignDeliverable?>> find,
        string userId,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        var deliverable = await find(false);
        if (deliverable is null)
        {
            return null;
        }

        var message = await dbContext.CampaignVideoChatMessages.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == messageId
                && candidate.OrganizationId == deliverable.OrganizationId
                && candidate.CampaignDeliverableId == deliverable.Id
                && candidate.Role == CampaignVideoChatRole.Assistant,
                cancellationToken);
        if (message?.EditsJson is null)
        {
            throw new InvalidOperationException("That message has no changes to render.");
        }

        if (message.VideoVersion != deliverable.Version)
        {
            throw new InvalidOperationException("The video has changed since these edits were suggested. Ask again in the chat.");
        }

        var revisions = await RevisionsAsync(deliverable, cancellationToken);
        if (revisions.Any(IsActive))
        {
            throw new InvalidOperationException("This video is already being updated.");
        }

        if (revisions.Any(revision => revision.ChatMessageId == message.Id
            && revision.Status != CampaignVideoRevisionStatus.Failed))
        {
            throw new InvalidOperationException("These changes were already rendered.");
        }

        if (RendersLeft(revisions) <= 0)
        {
            throw new InvalidOperationException(
                $"This video has used its {CampaignVideoRevision.FreeRendersPerVideo} free updates. Generate an updated campaign for more changes.");
        }

        dbContext.CampaignVideoRevisions.Add(CampaignVideoRevision.Create(
            deliverable,
            message.Id,
            userId,
            message.EditsJson,
            timeProvider.GetUtcNow()));
        await dbContext.SaveChangesAsync(cancellationToken);
        return await ToChatAsync(deliverable, cancellationToken);
    }

    private async Task<CampaignVideoChat?> ConfirmFixCoreAsync(
        Func<bool, Task<CampaignDeliverable?>> find,
        Guid messageId,
        bool worked,
        string? feedback,
        CancellationToken cancellationToken)
    {
        var deliverable = await find(false);
        if (deliverable is null)
        {
            return null;
        }

        var revision = await dbContext.CampaignVideoRevisions
            .Where(candidate => candidate.OrganizationId == deliverable.OrganizationId
                && candidate.CampaignDeliverableId == deliverable.Id
                && candidate.ChatMessageId == messageId
                && (candidate.Status == CampaignVideoRevisionStatus.Completed
                    || candidate.Status == CampaignVideoRevisionStatus.Undone))
            .OrderByDescending(candidate => candidate.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Those changes haven't been rendered yet.");
        revision.ConfirmFix(worked, feedback, timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        return await ToChatAsync(deliverable, cancellationToken);
    }

    private async Task<CampaignVideoChat?> UndoCoreAsync(
        Func<bool, Task<CampaignDeliverable?>> find,
        CancellationToken cancellationToken)
    {
        var deliverable = await find(true);
        if (deliverable is null)
        {
            return null;
        }

        var organizationId = deliverable.OrganizationId;

        var revisions = await dbContext.CampaignVideoRevisions
            .Where(revision => revision.OrganizationId == organizationId && revision.CampaignDeliverableId == deliverable.Id)
            .ToListAsync(cancellationToken);
        if (revisions.Any(IsActive))
        {
            throw new InvalidOperationException("Wait for the current update to finish before undoing.");
        }

        var latest = revisions.SingleOrDefault(revision => revision.Status == CampaignVideoRevisionStatus.Completed
            && revision.ResultVersion == deliverable.Version)
            ?? throw new InvalidOperationException("There is no change to undo.");
        latest.Undo(deliverable, timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        return await ToChatAsync(deliverable, cancellationToken);
    }

    // The legacy address: one campaign-level video per campaign and output kind.
    private async Task<CampaignDeliverable?> FindAsync(
        string userId,
        Guid jobId,
        CampaignOutputKind kind,
        bool tracked,
        CancellationToken cancellationToken)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var query = tracked ? dbContext.CampaignDeliverables : dbContext.CampaignDeliverables.AsNoTracking();
        return await query
            .SingleOrDefaultAsync(candidate => candidate.CampaignGenerationJobId == jobId
                && candidate.OrganizationId == organizationId
                && candidate.Kind == kind
                && (candidate.CampaignVideoVariantId == null || candidate.CampaignVideoVariantId == candidate.Id)
                && candidate.Status == CampaignDeliverableStatus.Rendered,
                cancellationToken);
    }

    // Any rendered video in the workspace, library videos included, by its own id.
    private async Task<CampaignDeliverable?> FindByIdAsync(
        string userId,
        Guid deliverableId,
        bool tracked,
        CancellationToken cancellationToken)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var query = tracked ? dbContext.CampaignDeliverables : dbContext.CampaignDeliverables.AsNoTracking();
        return await query.SingleOrDefaultAsync(candidate => candidate.Id == deliverableId
                && candidate.OrganizationId == organizationId
                && candidate.Status == CampaignDeliverableStatus.Rendered,
            cancellationToken);
    }

    // A library video is named by its format, so the assistant talks about "the Price first video".
    private async Task<string> VideoNameAsync(CampaignDeliverable deliverable, CancellationToken cancellationToken)
    {
        if (deliverable.IsCampaignLevel)
        {
            return OutputName(deliverable.Kind);
        }

        var format = await dbContext.CampaignVideoVariants.AsNoTracking()
            .Where(variant => variant.Id == deliverable.CampaignVideoVariantId && variant.OrganizationId == deliverable.OrganizationId)
            .Select(variant => (VerticalCreativeFormat?)variant.Format)
            .SingleOrDefaultAsync(cancellationToken);
        return format is { } value
            ? $"{VerticalFormatNames.Name(value)} vertical video (TikTok, Reels, Shorts)"
            : OutputName(deliverable.Kind);
    }

    private Task<List<CampaignVideoRevision>> RevisionsAsync(
        CampaignDeliverable deliverable,
        CancellationToken cancellationToken) =>
        dbContext.CampaignVideoRevisions.AsNoTracking()
            .Where(revision => revision.OrganizationId == deliverable.OrganizationId
                && revision.CampaignDeliverableId == deliverable.Id)
            .OrderBy(revision => revision.CreatedAtUtc)
            .ToListAsync(cancellationToken);

    private async Task<CampaignVideoChat> ToChatAsync(
        CampaignDeliverable deliverable,
        CancellationToken cancellationToken)
    {
        var current = await dbContext.CampaignDeliverables.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == deliverable.Id, cancellationToken);
        var messages = await dbContext.CampaignVideoChatMessages.AsNoTracking()
            .Where(message => message.OrganizationId == current.OrganizationId
                && message.CampaignDeliverableId == current.Id)
            .OrderBy(message => message.CreatedAtUtc)
            .ToListAsync(cancellationToken);
        var revisions = await RevisionsAsync(current, cancellationToken);
        var active = revisions.LastOrDefault(IsActive);
        var latest = revisions.LastOrDefault();
        var byMessage = revisions
            .GroupBy(revision => revision.ChatMessageId)
            .ToDictionary(group => group.Key, group => group.Last());
        return new CampaignVideoChat(
            current.CampaignGenerationJobId,
            current.Kind,
            current.Version,
            RendersLeft(revisions),
            active is null && revisions.Any(revision => revision.Status == CampaignVideoRevisionStatus.Completed
                && revision.ResultVersion == current.Version),
            active?.Status,
            latest?.Status == CampaignVideoRevisionStatus.Failed ? latest.LastError : null,
            messages.Select(message =>
            {
                var proposal = message.EditsJson is null ? null : CampaignVideoEditing.Deserialize(message.EditsJson);
                byMessage.TryGetValue(message.Id, out var rendered);
                var state = proposal is null
                    ? CampaignVideoChatProposalState.None
                    : byMessage.TryGetValue(message.Id, out var revision)
                        ? revision.Status switch
                        {
                            CampaignVideoRevisionStatus.Queued or CampaignVideoRevisionStatus.Running => CampaignVideoChatProposalState.Rendering,
                            CampaignVideoRevisionStatus.Completed => CampaignVideoChatProposalState.Rendered,
                            CampaignVideoRevisionStatus.Undone => CampaignVideoChatProposalState.Undone,
                            _ => message.VideoVersion == current.Version
                                ? CampaignVideoChatProposalState.Failed
                                : CampaignVideoChatProposalState.Outdated,
                        }
                        : message.VideoVersion == current.Version
                            ? CampaignVideoChatProposalState.Ready
                            : CampaignVideoChatProposalState.Outdated;
                return new CampaignVideoChatEntry(
                    message.Id,
                    message.Role,
                    message.Text,
                    proposal?.Changes ?? [],
                    state,
                    message.CreatedAtUtc,
                    rendered?.FixConfirmed,
                    rendered?.FixFeedback);
            }).ToArray(),
            current.Id);
    }

    private static bool IsActive(CampaignVideoRevision revision) =>
        revision.Status is CampaignVideoRevisionStatus.Queued or CampaignVideoRevisionStatus.Running;

    private static int RendersLeft(IEnumerable<CampaignVideoRevision> revisions) => Math.Max(
        0,
        CampaignVideoRevision.FreeRendersPerVideo
            - revisions.Count(revision => revision.Status != CampaignVideoRevisionStatus.Failed));

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

    public static string OutputName(CampaignOutputKind kind) => kind switch
    {
        CampaignOutputKind.TikTok => "Vertical social video (TikTok, Reels, Shorts)",
        CampaignOutputKind.InstagramReel => "Instagram Reel",
        CampaignOutputKind.YouTubeShort => "YouTube Short",
        CampaignOutputKind.YouTubeTour => "YouTube tour",
        CampaignOutputKind.FacebookFeed => "Facebook feed video",
        CampaignOutputKind.FullNarratedTour => "Full narrated tour",
        CampaignOutputKind.Hero => "Hero video",
        CampaignOutputKind.Feature => "Feature video",
        CampaignOutputKind.Teaser => "Teaser video",
        CampaignOutputKind.LongForm => "Long-form tour",
        _ => kind.ToString(),
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "The video chat assistant failed for campaign video {DeliverableId}")]
    private partial void LogAssistantFailed(Exception exception, Guid deliverableId);
}
