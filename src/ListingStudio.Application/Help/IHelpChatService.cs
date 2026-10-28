using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Help;

namespace ListingStudio.Application.Help;

/// <summary>
/// The "Ask AI" chat on every signed-in page. It answers questions about using Listing Studio; questions about one
/// campaign video, and changes to it, go to that video's own chat (<see cref="Campaigns.ICampaignVideoChatService"/>).
/// </summary>
public interface IHelpChatService
{
    public const int MaximumMessageLength = 1_000;

    /// <summary>Each message costs an AI call, so a user can send this many a day.</summary>
    public const int MaximumMessagesPerDay = 40;

    /// <summary>A conversation idle for longer than this starts fresh when the chat is opened again.</summary>
    public static readonly TimeSpan ConversationIdleTimeout = TimeSpan.FromHours(12);

    /// <summary>The user's current conversation, or an empty one.</summary>
    Task<HelpChat> GetAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends the user's message and returns the conversation with the assistant's answer. A null conversation starts
    /// a new one.
    /// </summary>
    Task<HelpChat> SendAsync(
        string userId,
        Guid? conversationId,
        string text,
        string? pagePath,
        Guid? propertyId,
        CancellationToken cancellationToken = default);

    /// <summary>The finished videos of the property's latest campaign, which the user can ask about one by one.</summary>
    Task<IReadOnlyList<HelpChatVideo>> GetVideosAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default);
}

public sealed record HelpChat(Guid? ConversationId, int MessagesLeftToday, IReadOnlyList<HelpChatEntry> Messages);

public sealed record HelpChatEntry(Guid Id, HelpChatRole Role, string Text, DateTimeOffset CreatedAtUtc);

public sealed record HelpChatVideo(Guid JobId, CampaignOutputKind Kind, string Name);

/// <summary>Answers questions about using Listing Studio from its built-in product guide.</summary>
public interface IHelpChatAssistant
{
    Task<string> ReplyAsync(HelpChatRequest request, CancellationToken cancellationToken = default);
}

public sealed record HelpChatTurn(HelpChatRole Role, string Text);

/// <summary>What the assistant knows about the property whose pages the user is on.</summary>
public sealed record HelpChatPropertySummary(
    string Address,
    int TourPhotoCount,
    int ExtraPhotoCount,
    string? LatestCampaignStatus,
    string? LatestCampaignProblem,
    IReadOnlyList<string> FinishedVideos);

public sealed record HelpChatRequest(
    string? PagePath,
    HelpChatPropertySummary? Property,
    IReadOnlyList<HelpChatTurn> Conversation);
