using ListingStudio.Application.Properties;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Properties;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ListingStudio.Infrastructure.Campaigns;

/// <summary>What the Kling 4 native-presenter request is built from, before any photo is opened or uploaded.</summary>
public sealed record NativePresenterPlanInput(
    Guid OrganizationId,
    Guid PropertyId,
    VerticalCampaignGoal? Goal,
    CampaignMarketingTemplate MarketingTemplate,
    string? OpenHouseText,
    CampaignSoundtrack Soundtrack);

public sealed record NativePresenterPlanFrame(
    Guid PropertyMediaId,
    string BlobPath,
    string MimeType,
    string StopName,
    string? ApprovedHighlight,
    WalkthroughDirection? DirectionToNext);

public sealed record NativePresenterPlan(
    IReadOnlyList<NativePresenterPlanFrame> Frames,
    string OpeningLine,
    string ClosingLine,
    string CampaignGoal,
    string MusicDirection);

/// <summary>
/// Builds the Kling 4 native-presenter request from the approved Tour Map. The Worker uses it to submit the paid call
/// and the admin preview uses it to show the same request for free, so both always agree.
/// </summary>
public static class NativePresenterRequestPlanner
{
    public const string ClosingLine = "Contact me today to schedule your private showing.";

    public static async Task<NativePresenterPlan> BuildAsync(
        ApplicationDbContext dbContext,
        NativePresenterPlanInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(input);
        var media = await dbContext.PropertyMedia.AsNoTracking()
            .Where(candidate => candidate.OrganizationId == input.OrganizationId
                && candidate.PropertyId == input.PropertyId
                && !candidate.IsInGallery
                && candidate.WalkthroughStop != null)
            .OrderBy(candidate => candidate.DisplayOrder)
            .ThenBy(candidate => candidate.Id)
            .Take(NativePresenterVideoPolicy.MaximumTourFrames)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.BlobPath,
                candidate.MimeType,
                StopName = candidate.WalkthroughStop!,
                candidate.AnalysisDescription,
            })
            .ToArrayAsync(cancellationToken);
        var directions = (await dbContext.WalkthroughStopDirections.AsNoTracking()
                .Where(candidate => candidate.OrganizationId == input.OrganizationId
                    && candidate.PropertyId == input.PropertyId)
                .Select(candidate => new { candidate.Stop, candidate.Direction })
                .ToArrayAsync(cancellationToken))
            .GroupBy(candidate => candidate.Stop, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Direction, StringComparer.OrdinalIgnoreCase);
        var narration = (await dbContext.WalkthroughStopNarrations.AsNoTracking()
                .Where(candidate => candidate.OrganizationId == input.OrganizationId
                    && candidate.PropertyId == input.PropertyId)
                .Select(candidate => new { candidate.Stop, candidate.Text })
                .ToArrayAsync(cancellationToken))
            .GroupBy(candidate => candidate.Stop, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(candidate => candidate.Text.Length).First().Text,
                StringComparer.OrdinalIgnoreCase);
        var frames = new List<NativePresenterPlanFrame>(media.Length);
        for (var index = 0; index < media.Length; index++)
        {
            var item = media[index];
            var direction = index + 1 < media.Length
                ? WalkthroughClipDirection.ForPair(item.StopName, media[index + 1].StopName, directions)
                : null;
            frames.Add(new NativePresenterPlanFrame(
                item.Id,
                item.BlobPath,
                item.MimeType,
                item.StopName,
                narration.TryGetValue(item.StopName, out var text) ? Excerpt(text) : Excerpt(item.AnalysisDescription),
                direction));
        }

        var property = await dbContext.Properties.AsNoTracking()
            .Where(candidate => candidate.Id == input.PropertyId && candidate.OrganizationId == input.OrganizationId)
            .Select(candidate => new { candidate.City, candidate.State, candidate.YearBuilt })
            .SingleAsync(cancellationToken);
        return new NativePresenterPlan(
            frames,
            $"Welcome to your next home in {property.City}, {property.State}. Let me show you around.",
            ClosingLine,
            Goal(input),
            Music(input.Soundtrack, property.YearBuilt));
    }

    private static string Goal(NativePresenterPlanInput input) => input.Goal switch
    {
        VerticalCampaignGoal.MaximizeReach => "Maximize reach with an energetic, immediately engaging property tour.",
        VerticalCampaignGoal.BuyerInterest => "Generate serious buyer interest by emphasizing only approved property details.",
        VerticalCampaignGoal.OpenHouse => $"Promote the verified open house: {input.OpenHouseText}.",
        VerticalCampaignGoal.NewListing => "Announce a newly listed home with an inviting first look.",
        VerticalCampaignGoal.PriceChange => "Announce the verified price change without making a value claim.",
        _ => input.MarketingTemplate switch
        {
            CampaignMarketingTemplate.OpenHouse => $"Promote the verified open house: {input.OpenHouseText}.",
            CampaignMarketingTemplate.JustListed => "Announce a newly listed home with an inviting first look.",
            CampaignMarketingTemplate.PriceDrop => "Announce the verified price change without making a value claim.",
            CampaignMarketingTemplate.FeatureSpotlight => "Highlight the most interesting approved property details.",
            CampaignMarketingTemplate.QuickTour => "Maximize reach with a concise, energetic property tour.",
            _ => "Create buyer interest with a polished, factual tour of the home.",
        },
    };

    private static string Music(CampaignSoundtrack soundtrack, int? yearBuilt)
    {
        if (soundtrack == CampaignSoundtrack.AutoByHomeEra)
        {
            soundtrack = yearBuilt switch
            {
                < 1965 => CampaignSoundtrack.HeritageWarm,
                < 2000 => CampaignSoundtrack.UpbeatLifestyle,
                _ => CampaignSoundtrack.ModernAmbient,
            };
        }

        return soundtrack switch
        {
            CampaignSoundtrack.HeritageWarm => "Warm acoustic instrumental appropriate to the home's era; no lyrics.",
            CampaignSoundtrack.UpbeatLifestyle => "Upbeat lifestyle instrumental with a contemporary pulse; no lyrics.",
            CampaignSoundtrack.LuxuryCinematic => "Refined cinematic piano and strings for a luxury property; no lyrics.",
            CampaignSoundtrack.None or CampaignSoundtrack.AddInApp =>
                "Very subtle neutral instrumental bed; no lyrics, and keep it well below the agent's speech.",
            _ => "Modern ambient instrumental with a confident real-estate-tour pace; no lyrics.",
        };
    }

    private static string? Excerpt(string? text)
    {
        var normalized = WalkthroughStopNarration.Normalize(text);
        return normalized is null ? null : normalized[..Math.Min(normalized.Length, 240)];
    }
}
