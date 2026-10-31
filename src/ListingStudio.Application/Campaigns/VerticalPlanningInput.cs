using System.Globalization;
using ListingStudio.Domain.Properties;

namespace ListingStudio.Application.Campaigns;

/// <summary>Verified listing facts, as entered by the agent.</summary>
public sealed record VerticalListingFacts(
    PropertyType PropertyType,
    decimal ListingPrice,
    int Bedrooms,
    decimal Bathrooms,
    int? SquareFeet,
    decimal? LotSize,
    string? City,
    string? State,
    int ApprovedNeighborhoodPlaces)
{
    private static readonly CultureInfo UnitedStates = CultureInfo.GetCultureInfo("en-US");

    public bool HasCityAndState => !string.IsNullOrWhiteSpace(City) && !string.IsNullOrWhiteSpace(State);

    public string? PriceText => ListingPrice > 0 ? ListingPrice.ToString("C0", UnitedStates) : null;

    public string? CityStateText => HasCityAndState ? $"{City!.Trim()}, {State!.Trim()}" : null;

    /// <summary>"3 beds • 2.5 baths • 2,100 sq ft", or the lot size for land; null when the core facts are missing.</summary>
    public string? EssentialFactsText
    {
        get
        {
            var parts = new List<string>();
            if (PropertyType == PropertyType.Land)
            {
                if (LotSize > 0)
                {
                    parts.Add($"{LotSize.Value.ToString("0.##", UnitedStates)} acre lot");
                }
            }
            else if (Bedrooms > 0 && Bathrooms > 0)
            {
                parts.Add($"{Bedrooms} bed{(Bedrooms == 1 ? string.Empty : "s")}");
                parts.Add($"{Bathrooms.ToString("0.#", UnitedStates)} bath{(Bathrooms == 1 ? string.Empty : "s")}");
                if (SquareFeet > 0)
                {
                    parts.Add($"{SquareFeet.Value.ToString("N0", UnitedStates)} sq ft");
                }
            }

            return parts.Count == 0 ? null : string.Join(" • ", parts);
        }
    }
}

/// <summary>One storyboard photo with its analysis and the roles in force.</summary>
public sealed record VerticalPlanningPhoto(
    Guid MediaId,
    string? Stop,
    PropertyMediaCategory? Category,
    bool AnalysisCompleted,
    int? QualityScore,
    int? HeroScore,
    bool? IsExterior,
    int Width,
    int Height,
    int DisplayOrder,
    IReadOnlyList<VerticalMediaRoleState> Roles)
{
    public bool Has(VerticalMediaRole role) => Roles.Any(state => state.Role == role);

    public bool HasFromAgent(VerticalMediaRole role) =>
        Roles.Any(state => state.Role == role && state.Source == VerticalMediaRoleSource.Agent);

    /// <summary>Same rule as the recommendation engine.</summary>
    public bool IsUsable => AnalysisCompleted ? QualityScore >= 50 : Math.Min(Width, Height) >= 720;
}

/// <summary>One property video with the roles in force.</summary>
public sealed record VerticalPlanningFootage(
    Guid VideoId,
    int DurationMs,
    bool IsReady,
    bool HasAudio,
    PropertyMediaCategory? TourStop,
    int TourOrder,
    IReadOnlyList<VerticalMediaRoleState> Roles)
{
    public bool Has(VerticalMediaRole role) => Roles.Any(state => state.Role == role);

    public bool HasFromAgent(VerticalMediaRole role) =>
        Roles.Any(state => state.Role == role && state.Source == VerticalMediaRoleSource.Agent);

    /// <summary>Footage a host can lead with: tagged as an intro, a hosted walk-through or a live replay.</summary>
    public bool IsHostFootage => IsReady
        && (Has(VerticalMediaRole.AgentIntro) || Has(VerticalMediaRole.HostedWalkthrough) || Has(VerticalMediaRole.LiveReplay));
}

/// <summary>A line of the agent's approved narration, spoken word for word.</summary>
public sealed record VerticalNarrationLine(string GroundingKey, string Text, string? Stop);

/// <summary>An agent-confirmed story claim, in the agent's words.</summary>
public sealed record VerticalStoryClaimInput(Guid ClaimId, string Problem, string Payoff);

/// <summary>Agent-confirmed access or timing language, in the agent's words.</summary>
public sealed record VerticalAccessClaimInput(Guid ClaimId, string AccessText);

/// <summary>Everything the readiness evaluator and the format planners read for one listing.</summary>
public sealed record VerticalPlanningInput(
    Guid PropertyId,
    VerticalListingFacts Facts,
    IReadOnlyList<VerticalPlanningPhoto> Photos,
    IReadOnlyList<VerticalPlanningFootage> Footage,
    IReadOnlyList<VerticalNarrationLine> Narration,
    VerticalPresenterInput Presenter,
    decimal? VerifiedPriceChange,
    VerticalStoryClaimInput? StoryClaim,
    VerticalAccessClaimInput? AccessClaim,
    VerticalUnconfirmedMentions Mentions)
{
    /// <summary>Usable storyboard photos in walking order.</summary>
    public IReadOnlyList<VerticalPlanningPhoto> UsablePhotos =>
        Photos.Where(photo => photo.IsUsable).OrderBy(photo => photo.DisplayOrder).ThenBy(photo => photo.MediaId).ToArray();

    /// <summary>
    /// Usable photos that show the home as it is now: everything except photos the agent tagged as before a
    /// renovation, which only a Story Reveal may show.
    /// </summary>
    public IReadOnlyList<VerticalPlanningPhoto> CurrentPhotos =>
        UsablePhotos.Where(photo => !photo.HasFromAgent(VerticalMediaRole.BeforeRenovation)).ToArray();

    /// <summary>The same facts in the shape the recommendation rules read.</summary>
    public VerticalFormatInputs ToRecommendationInputs() => new(
        Facts.PropertyType,
        Facts.ListingPrice,
        Facts.Bedrooms,
        Facts.Bathrooms,
        Facts.SquareFeet,
        Facts.LotSize,
        Facts.HasCityAndState,
        Facts.ApprovedNeighborhoodPlaces,
        Photos.Select(photo => new VerticalPhotoInput(
            photo.MediaId,
            photo.Category,
            photo.AnalysisCompleted,
            photo.QualityScore,
            photo.HeroScore,
            photo.Width,
            photo.Height,
            photo.HasFromAgent(VerticalMediaRole.BeforeRenovation) ? VerticalPhotoStoryRole.Before
            : photo.HasFromAgent(VerticalMediaRole.AfterRenovation) ? VerticalPhotoStoryRole.After
            : VerticalPhotoStoryRole.None)).ToArray(),
        Footage.Select(video => new VerticalFootageInput(video.VideoId, video.DurationMs, video.IsReady, video.IsHostFootage))
            .ToArray(),
        Narration.Count > 0,
        Presenter,
        new VerticalAgentConfirmations(
            VerifiedPriceChange > 0,
            StoryClaim is not null,
            StoryClaim is not null,
            AccessClaim is not null),
        Mentions);
}
