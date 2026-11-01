using System.Globalization;
using System.Text;

namespace ListingStudio.Domain.Properties;

/// <summary>Claims a vertical video may make only after the agent confirms them. Stored by name; append new ones.</summary>
public enum ListingCreativeClaimKind
{
    /// <summary>The problem the home had and the result: the basis of a Story Reveal video.</summary>
    StoryReveal,

    /// <summary>Private, early or time-limited access: the basis of an Exclusive Access video.</summary>
    ExclusiveAccess,
}

/// <summary>
/// An agent's confirmation of a claim, in the agent's own words. Rows are append-only: the latest row for a property
/// and kind is in force, a withdrawal is a new row, and earlier rows stay as the audit trail.
/// </summary>
public sealed class ListingCreativeClaim
{
    public const int MaximumTextLength = 100;

    private ListingCreativeClaim() { }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid PropertyId { get; private set; }

    public ListingCreativeClaimKind Kind { get; private set; }

    /// <summary>Story: the problem, as the agent states it. Access: the access or timing language.</summary>
    public string? PrimaryText { get; private set; }

    /// <summary>Story: the result and payoff. Unused for access.</summary>
    public string? SecondaryText { get; private set; }

    /// <summary>Access claims stop being usable after this time.</summary>
    public DateTimeOffset? ValidUntilUtc { get; private set; }

    public bool IsWithdrawn { get; private set; }

    public string ConfirmedByUserId { get; private set; } = string.Empty;

    public DateTimeOffset ConfirmedAtUtc { get; private set; }

    public bool IsActiveAt(DateTimeOffset now) => !IsWithdrawn && (ValidUntilUtc is null || ValidUntilUtc > now);

    public static ListingCreativeClaim ConfirmStory(
        Guid organizationId,
        Guid propertyId,
        string problem,
        string payoff,
        string userId,
        DateTimeOffset now) =>
        Create(organizationId, propertyId, ListingCreativeClaimKind.StoryReveal,
            Required(problem, nameof(problem), "Describe the problem the home had."),
            Required(payoff, nameof(payoff), "Describe the result."),
            null, false, userId, now);

    public static ListingCreativeClaim ConfirmAccess(
        Guid organizationId,
        Guid propertyId,
        string accessText,
        DateTimeOffset? validUntilUtc,
        string userId,
        DateTimeOffset now)
    {
        if (validUntilUtc is { } until && until <= now)
        {
            throw new ArgumentException("The access date must be in the future.", nameof(validUntilUtc));
        }

        return Create(organizationId, propertyId, ListingCreativeClaimKind.ExclusiveAccess,
            Required(accessText, nameof(accessText), "Write the access or timing language."),
            null, validUntilUtc, false, userId, now);
    }

    public static ListingCreativeClaim Withdraw(
        Guid organizationId,
        Guid propertyId,
        ListingCreativeClaimKind kind,
        string userId,
        DateTimeOffset now) =>
        Create(organizationId, propertyId, kind, null, null, null, true, userId, now);

    private static ListingCreativeClaim Create(
        Guid organizationId,
        Guid propertyId,
        ListingCreativeClaimKind kind,
        string? primary,
        string? secondary,
        DateTimeOffset? validUntil,
        bool withdrawn,
        string userId,
        DateTimeOffset now)
    {
        if (organizationId == Guid.Empty || propertyId == Guid.Empty)
        {
            throw new ArgumentException("A workspace and property are required.");
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (string.IsNullOrWhiteSpace(userId) || userId.Length > 450)
        {
            throw new ArgumentException("The user identity is invalid.", nameof(userId));
        }

        return new ListingCreativeClaim
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            PropertyId = propertyId,
            Kind = kind,
            PrimaryText = primary,
            SecondaryText = secondary,
            ValidUntilUtc = validUntil,
            IsWithdrawn = withdrawn,
            ConfirmedByUserId = userId,
            ConfirmedAtUtc = now,
        };
    }

    /// <summary>One line of plain text, at most 100 characters, so it can be a hook or an on-screen line.</summary>
    private static string Required(string? value, string name, string message)
    {
        var builder = new StringBuilder();
        var space = false;
        foreach (var character in (value ?? string.Empty).Normalize(NormalizationForm.FormC))
        {
            var category = char.GetUnicodeCategory(character);
            if (char.IsWhiteSpace(character) || category is UnicodeCategory.Control or UnicodeCategory.Format)
            {
                space = builder.Length > 0;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(character);
        }

        var text = builder.ToString();
        if (text.Length == 0)
        {
            throw new ArgumentException(message, name);
        }

        if (text.Length > MaximumTextLength)
        {
            throw new ArgumentException($"Keep it under {MaximumTextLength} characters.", name);
        }

        return text;
    }
}
