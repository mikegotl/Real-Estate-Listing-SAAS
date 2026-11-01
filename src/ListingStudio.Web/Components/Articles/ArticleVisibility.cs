using System.Security.Claims;
using ListingStudio.Application.Administration;
using ListingStudio.Application.Campaigns;

namespace ListingStudio.Web.Components.Articles;

/// <summary>
/// Decides who can see a feature article, through the central feature switch. Public visitors (and the sitemap) see it
/// only when the feature is on for everyone; a signed-in member also sees it when the feature is on for their workspace.
/// With the global kill switch off nobody sees it.
/// </summary>
public sealed class ArticleVisibility(IFeatureStateService features, ICampaignGenerationService campaigns)
{
    public async Task<bool> IsPublicAsync(string? featureKey, CancellationToken cancellationToken = default)
    {
        if (featureKey is null)
        {
            return true;
        }

        var state = await features.GetAsync(featureKey, cancellationToken);
        return state.IsGloballyEnabled && state.IsEnabledForAllOrganizations;
    }

    public async Task<bool> CanSeeAsync(ArticleEntry entry, ClaimsPrincipal? user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (await IsPublicAsync(entry.Summary.FeatureKey, cancellationToken))
        {
            return true;
        }

        return await IsOnForUserAsync(entry.Summary.FeatureKey!, user, cancellationToken);
    }

    public async Task<IReadOnlyList<ArticleEntry>> VisibleAsync(ClaimsPrincipal? user, CancellationToken cancellationToken = default)
    {
        var visible = new List<ArticleEntry>();
        foreach (var entry in ArticleLibrary.All)
        {
            if (await CanSeeAsync(entry, user, cancellationToken))
            {
                visible.Add(entry);
            }
        }

        return visible;
    }

    /// <summary>Public entries only: what the sitemap lists.</summary>
    public async Task<IReadOnlyList<ArticleEntry>> PublicAsync(CancellationToken cancellationToken = default)
    {
        var visible = new List<ArticleEntry>();
        foreach (var entry in ArticleLibrary.All)
        {
            if (await IsPublicAsync(entry.Summary.FeatureKey, cancellationToken))
            {
                visible.Add(entry);
            }
        }

        return visible;
    }

    /// <summary>Whether the vertical video library's copy (headline, links) applies to this visitor.</summary>
    public async Task<bool> IsVerticalLibraryOnAsync(ClaimsPrincipal? user, CancellationToken cancellationToken = default) =>
        await IsPublicAsync(ListingStudio.Domain.Administration.FeatureFlagKeys.VerticalVideoLibraryV1, cancellationToken)
        || await IsOnForUserAsync(ListingStudio.Domain.Administration.FeatureFlagKeys.VerticalVideoLibraryV1, user, cancellationToken);

    private async Task<bool> IsOnForUserAsync(string featureKey, ClaimsPrincipal? user, CancellationToken cancellationToken)
    {
        var userId = user?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)
            || !string.Equals(featureKey, ListingStudio.Domain.Administration.FeatureFlagKeys.VerticalVideoLibraryV1, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            return await campaigns.IsVerticalLibraryEnabledAsync(userId, cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
