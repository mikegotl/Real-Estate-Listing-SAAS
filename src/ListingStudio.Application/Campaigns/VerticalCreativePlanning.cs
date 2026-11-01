using ListingStudio.Domain.Properties;

namespace ListingStudio.Application.Campaigns;

/// <summary>Recommendation, readiness and one recipe per eligible format, all from the same loaded inputs.</summary>
public sealed record VerticalCreativePlan(
    VerticalPlanningInput Input,
    VerticalFormatRecommendationSet Recommendation,
    IReadOnlyList<VerticalFormatReadiness> Readiness,
    IReadOnlyList<VerticalCreativeRecipe> Recipes)
{
    public static VerticalCreativePlan Create(VerticalPlanningInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var recommendation = VerticalFormatRules.Evaluate(input.ToRecommendationInputs());
        return new VerticalCreativePlan(
            input,
            recommendation,
            VerticalReadinessEvaluator.Evaluate(input, recommendation),
            VerticalRecipePlanner.PlanEligible(input, recommendation));
    }
}

/// <summary>Plans the vertical formats for one listing. Reads only.</summary>
public interface IVerticalCreativePlanningService
{
    Task<VerticalCreativePlan?> PlanAsync(
        string userId,
        Guid propertyId,
        VerticalRecommendationContext context,
        CancellationToken cancellationToken = default);

    Task<VerticalCreativePlan?> PlanForOrganizationAsync(
        Guid organizationId,
        Guid propertyId,
        VerticalRecommendationContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>A photo or property video with the roles in force on it.</summary>
public sealed record VerticalMediaRoleItem(Guid TargetId, bool IsVideo, string? Label, IReadOnlyList<VerticalMediaRoleState> Roles);

/// <summary>One agent decision in the audit trail.</summary>
public sealed record VerticalMediaRoleHistoryEntry(
    Guid TargetId,
    VerticalMediaRole Role,
    VerticalMediaRoleDecision Decision,
    string SetByUserId,
    DateTimeOffset SetAtUtc);

/// <summary>Suggested and agent-chosen media roles. The agent's choice wins and every change is kept.</summary>
public interface IVerticalMediaRoleService
{
    /// <summary>Every storyboard photo and property video with its roles, or null when the property is not found.</summary>
    Task<IReadOnlyList<VerticalMediaRoleItem>?> GetAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default);

    /// <summary>Records a decision for one role on one photo or video; false when it is not in this workspace.</summary>
    Task<bool> SetAsync(
        string userId,
        Guid propertyId,
        Guid targetId,
        VerticalMediaRole role,
        VerticalMediaRoleDecision decision,
        CancellationToken cancellationToken = default);

    /// <summary>Every decision for the property, newest first.</summary>
    Task<IReadOnlyList<VerticalMediaRoleHistoryEntry>> GetHistoryAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default);
}

/// <summary>A claim in force, in the agent's words.</summary>
public sealed record ListingCreativeClaimState(
    ListingCreativeClaimKind Kind,
    string? PrimaryText,
    string? SecondaryText,
    DateTimeOffset? ValidUntilUtc,
    string ConfirmedByUserId,
    DateTimeOffset ConfirmedAtUtc);

/// <summary>Agent-confirmed story and access claims. Confirmations and withdrawals are kept as history.</summary>
public interface IListingCreativeClaimService
{
    /// <summary>The claims in force, or null when the property is not found.</summary>
    Task<IReadOnlyList<ListingCreativeClaimState>?> GetActiveAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default);

    Task<bool> ConfirmStoryAsync(
        string userId,
        Guid propertyId,
        string problem,
        string payoff,
        CancellationToken cancellationToken = default);

    Task<bool> ConfirmAccessAsync(
        string userId,
        Guid propertyId,
        string accessText,
        DateTimeOffset? validUntilUtc,
        CancellationToken cancellationToken = default);

    Task<bool> WithdrawAsync(
        string userId,
        Guid propertyId,
        ListingCreativeClaimKind kind,
        CancellationToken cancellationToken = default);
}
