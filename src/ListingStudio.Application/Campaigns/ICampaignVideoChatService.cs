using ListingStudio.Application.Videos;
using ListingStudio.Domain.Campaigns;

namespace ListingStudio.Application.Campaigns;

/// <summary>The chat beside each finished campaign video, where the user asks for changes to just that video.</summary>
public interface ICampaignVideoChatService
{
    public const int MaximumMessageLength = 1_000;

    Task<CampaignVideoChat?> GetAsync(
        string userId,
        Guid jobId,
        CampaignOutputKind kind,
        CancellationToken cancellationToken = default);

    /// <summary>Sends the user's message and returns the chat with the assistant's answer.</summary>
    Task<CampaignVideoChat?> SendAsync(
        string userId,
        Guid jobId,
        CampaignOutputKind kind,
        string text,
        CancellationToken cancellationToken = default);

    /// <summary>Queues a render of the changes an assistant message proposed.</summary>
    Task<CampaignVideoChat?> RenderAsync(
        string userId,
        Guid jobId,
        CampaignOutputKind kind,
        Guid messageId,
        CancellationToken cancellationToken = default);

    /// <summary>Records the user's answer to "Did this fix it?" for the change an assistant message proposed.</summary>
    Task<CampaignVideoChat?> ConfirmFixAsync(
        string userId,
        Guid jobId,
        CampaignOutputKind kind,
        Guid messageId,
        bool worked,
        string? feedback,
        CancellationToken cancellationToken = default);

    /// <summary>Puts the video back to the version before its latest change.</summary>
    Task<CampaignVideoChat?> UndoAsync(
        string userId,
        Guid jobId,
        CampaignOutputKind kind,
        CancellationToken cancellationToken = default);
}

public sealed record CampaignVideoChat(
    Guid JobId,
    CampaignOutputKind Kind,
    int Version,
    int RendersLeft,
    bool CanUndo,
    CampaignVideoRevisionStatus? ActiveRevisionStatus,
    string? LastRevisionError,
    IReadOnlyList<CampaignVideoChatEntry> Messages);

public sealed record CampaignVideoChatEntry(
    Guid Id,
    CampaignVideoChatRole Role,
    string Text,
    IReadOnlyList<string> ProposedChanges,
    CampaignVideoChatProposalState Proposal,
    DateTimeOffset CreatedAtUtc,
    bool? FixConfirmed = null,
    string? FixFeedback = null);

public enum CampaignVideoChatProposalState
{
    None,
    Ready,
    Rendering,
    Rendered,
    Failed,
    Undone,
    Outdated,
}

/// <summary>Turns the user's words about one video into a reply and edits from the fixed edit menu.</summary>
public interface ICampaignVideoChatAssistant
{
    Task<CampaignVideoChatReply> ReplyAsync(
        CampaignVideoChatRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record CampaignVideoChatTurn(CampaignVideoChatRole Role, string Text);

public sealed record CampaignVideoFact(string Key, string Value);

public sealed record CampaignVideoChatRequest(
    string VideoName,
    int DurationSeconds,
    string AspectRatio,
    IReadOnlyList<CampaignVideoShotSummary> Shots,
    IReadOnlyList<CampaignVideoPhoto> ListingPhotos,
    IReadOnlyList<CampaignVideoFact> Facts,
    CampaignSoundtrack? CurrentMusic,
    decimal MusicGainDb,
    bool HasVoiceover,
    CampaignVoiceAccent? CurrentVoice,
    CampaignCaptionStyle? CaptionStyle,
    int RendersLeft,
    IReadOnlyList<CampaignVideoChatTurn> Conversation);

public sealed record CampaignVideoChatReply(string Reply, IReadOnlyList<CampaignVideoEdit> Edits);
