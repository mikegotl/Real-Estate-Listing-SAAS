namespace ListingStudio.Domain.Help;

public enum HelpChatRole
{
    User,
    Assistant,
}

/// <summary>
/// One message in a user's site-wide help chat. Kept for the administrator review page, so it has no foreign keys
/// that could delete it with its property.
/// </summary>
public sealed class HelpChatMessage
{
    public const int MaximumTextLength = 2_000;
    public const int MaximumPagePathLength = 300;

    private HelpChatMessage()
    {
    }

    private HelpChatMessage(
        Guid organizationId,
        string userId,
        Guid conversationId,
        HelpChatRole role,
        string text,
        string? pagePath,
        Guid? propertyId,
        Guid? questionId,
        DateTimeOffset now)
    {
        if (organizationId == Guid.Empty || conversationId == Guid.Empty || propertyId == Guid.Empty)
        {
            throw new ArgumentException("Organization, conversation and property identities must not be empty.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }

        var normalized = text.Trim();
        var path = string.IsNullOrWhiteSpace(pagePath) ? null : pagePath.Trim();
        Id = Guid.NewGuid();
        OrganizationId = organizationId;
        UserId = userId.Length <= 450 ? userId : throw new ArgumentOutOfRangeException(nameof(userId));
        ConversationId = conversationId;
        Role = role;
        Text = normalized.Length <= MaximumTextLength ? normalized : normalized[..MaximumTextLength];
        PagePath = path is { Length: > MaximumPagePathLength } ? path[..MaximumPagePathLength] : path;
        PropertyId = propertyId;
        QuestionId = questionId;
        CreatedAtUtc = now;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    /// <summary>The user whose chat this is, on the assistant's answers too.</summary>
    public string UserId { get; private set; } = string.Empty;

    /// <summary>Starting over begins a new conversation; earlier ones stay for the administrator review page.</summary>
    public Guid ConversationId { get; private set; }

    public HelpChatRole Role { get; private set; }

    public string Text { get; private set; } = string.Empty;

    /// <summary>The page the user was on when they asked.</summary>
    public string? PagePath { get; private set; }

    /// <summary>The property whose pages the user was on, if any.</summary>
    public Guid? PropertyId { get; private set; }

    /// <summary>On an assistant message, the user message it answers.</summary>
    public Guid? QuestionId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static HelpChatMessage FromUser(
        Guid organizationId,
        string userId,
        Guid conversationId,
        string text,
        string? pagePath,
        Guid? propertyId,
        DateTimeOffset now) =>
        new(organizationId, userId, conversationId, HelpChatRole.User, text, pagePath, propertyId, null, now);

    public static HelpChatMessage FromAssistant(HelpChatMessage question, string text, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(question);
        return new HelpChatMessage(
            question.OrganizationId,
            question.UserId,
            question.ConversationId,
            HelpChatRole.Assistant,
            text,
            question.PagePath,
            question.PropertyId,
            question.Id,
            now);
    }
}
