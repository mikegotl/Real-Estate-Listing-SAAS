using System.Globalization;
using ListingStudio.Application.Audio;
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
    CampaignSoundtrack Soundtrack,
    NativePresenterScript? ApprovedScript = null,
    VerticalCreativeFormat? Format = null,
    int DurationSeconds = NativePresenterVideoPolicy.DefaultDurationSeconds,
    Guid? LeadMediaId = null);

/// <summary>The verified listing facts a suggested script may say.</summary>
public sealed record NativePresenterListing(
    string City,
    string State,
    decimal ListingPrice,
    int Bedrooms,
    decimal Bathrooms,
    int? SquareFeet);

public sealed record NativePresenterPlanFrame(
    Guid PropertyMediaId,
    string BlobPath,
    string MimeType,
    string StopName,
    string? ApprovedHighlight,
    WalkthroughDirection? DirectionToNext);

public sealed record NativePresenterPlan(
    IReadOnlyList<NativePresenterPlanFrame> Frames,
    NativePresenterScript Script,
    bool ScriptIsApproved,
    string CampaignGoal,
    string MusicDirection,
    int DurationSeconds = NativePresenterVideoPolicy.DefaultDurationSeconds);

/// <summary>
/// Builds the Kling 4 native-presenter request from the approved Tour Map. The Worker uses it to submit the paid call
/// and the admin preview uses it to show the same request for free, so both always agree. A vertical library video
/// gets its own request: its own length, its own opening shot and a script written for its format.
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
            .Select(candidate => new
            {
                candidate.Id,
                candidate.BlobPath,
                candidate.MimeType,
                StopName = candidate.WalkthroughStop!,
                candidate.AnalysisDescription,
            })
            .ToArrayAsync(cancellationToken);
        // The video opens on its format's lead shot when that photo is on the Tour Map, then follows the route.
        // The hosted walk-through follows the route from its first stop, so it never jumps to a lead shot.
        if (input.Format != VerticalCreativeFormat.HostedPov
            && input.LeadMediaId is { } leadId
            && media.FirstOrDefault(candidate => candidate.Id == leadId) is { } lead)
        {
            media = [lead, .. media.Where(candidate => candidate.Id != leadId)];
        }

        media = media.Take(NativePresenterVideoPolicy.MaximumTourFramesFor(input.DurationSeconds)).ToArray();
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
            .Select(candidate => new
            {
                candidate.City,
                candidate.State,
                candidate.YearBuilt,
                candidate.ListingPrice,
                candidate.Bedrooms,
                candidate.Bathrooms,
                candidate.SquareFeet,
            })
            .SingleAsync(cancellationToken);
        var script = input.ApprovedScript?.Normalized()
            ?? SuggestScript(
                new NativePresenterListing(
                    property.City,
                    property.State,
                    property.ListingPrice,
                    property.Bedrooms,
                    property.Bathrooms,
                    property.SquareFeet),
                frames,
                input);
        return new NativePresenterPlan(
            frames,
            script,
            input.ApprovedScript is not null,
            Goal(input),
            Music(input.Soundtrack, property.YearBuilt),
            input.DurationSeconds);
    }

    /// <summary>A starting script for the shared vertical video, from the city and Tour Map alone.</summary>
    public static NativePresenterScript SuggestScript(
        string city,
        string state,
        IReadOnlyList<NativePresenterPlanFrame> frames,
        NativePresenterPlanInput input) =>
        SuggestScript(new NativePresenterListing(city, state, 0, 0, 0, null), frames, input);

    /// <summary>
    /// A starting script the agent edits: an opening written for the video's format, one short line per Tour Map stop
    /// in order (its approved room narration's first sentence, or the stop's name), and a closing, kept inside the
    /// video's word budget. Listing shorthand such as state codes is written the way it is said.
    /// </summary>
    public static NativePresenterScript SuggestScript(
        NativePresenterListing listing,
        IReadOnlyList<NativePresenterPlanFrame> frames,
        NativePresenterPlanInput input)
    {
        ArgumentNullException.ThrowIfNull(listing);
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(input);
        var place = $"{listing.City.Trim()}, {listing.State.Trim()}";
        var lines = new List<string>();
        var opening = input.Format switch
        {
            VerticalCreativeFormat.PriceFirst when listing.ListingPrice > 0 =>
                $"This home in {place} is listed at {listing.ListingPrice.ToString("C0", UnitedStates)}.",
            VerticalCreativeFormat.WowFeature when frames.Count > 0 =>
                $"You have to see this {frames[0].StopName.Trim().ToLowerInvariant()} in {place}.",
            VerticalCreativeFormat.ExclusiveAccess => $"Here is a private look at a home in {place}.",
            _ => $"Welcome to your next home in {place}. Let me show you around.",
        };
        if (input.Format == VerticalCreativeFormat.PriceFirst && Facts(listing) is { } facts)
        {
            lines.Add(facts);
        }

        var closing = input.Goal == VerticalCampaignGoal.OpenHouse
            || (input.Goal is null && input.MarketingTemplate == CampaignMarketingTemplate.OpenHouse)
                ? $"Join me at the open house, {input.OpenHouseText}. Contact me to save your spot."
                : ClosingLine;
        opening = SpeechText.ForSpeech(opening);
        var budget = NativePresenterScript.MaximumWordsFor(input.DurationSeconds)
            - NativePresenterScript.CountWords(opening)
            - NativePresenterScript.CountWords(closing)
            - lines.Sum(NativePresenterScript.CountWords);
        // The Wow feature opening already names its first space, so its tour starts with the next one.
        foreach (var frame in input.Format == VerticalCreativeFormat.WowFeature ? frames.Skip(1) : frames)
        {
            var line = FirstSentence(frame.ApprovedHighlight) ?? $"Here is the {frame.StopName.Trim().ToLowerInvariant()}.";
            line = SpeechText.ForSpeech(line);
            var words = NativePresenterScript.CountWords(line);
            if (words > budget)
            {
                break;
            }

            lines.Add(line);
            budget -= words;
        }

        var tour = lines.Count > 0 ? string.Join(' ', lines) : "Take a look around this home.";
        return new NativePresenterScript(opening, tour, closing).Normalized();
    }

    private static readonly CultureInfo UnitedStates = CultureInfo.GetCultureInfo("en-US");

    private static string? Facts(NativePresenterListing listing)
    {
        var parts = new List<string>();
        if (listing.Bedrooms > 0)
        {
            parts.Add($"{listing.Bedrooms} {(listing.Bedrooms == 1 ? "bedroom" : "bedrooms")}");
        }

        if (listing.Bathrooms > 0)
        {
            parts.Add($"{listing.Bathrooms.ToString("0.#", UnitedStates)} {(listing.Bathrooms == 1 ? "bathroom" : "bathrooms")}");
        }

        if (listing.SquareFeet is { } squareFeet && squareFeet > 0)
        {
            parts.Add($"{squareFeet.ToString("N0", UnitedStates)} square feet");
        }

        return parts.Count == 0 ? null : SpeechText.ForSpeech(string.Join(", ", parts) + ".");
    }

    private static string? FirstSentence(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var clean = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Replace("\"", string.Empty, StringComparison.Ordinal);
        var end = clean.IndexOfAny(['.', '!', '?']);
        var sentence = end < 0 ? clean + "." : clean[..(end + 1)];
        return sentence.Length < 2 ? null : sentence;
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
