using System.Globalization;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Videos;

namespace ListingStudio.Application.Videos;

/// <summary>The on-screen text and voiceover lines of a short-form edit, each grounded in a fact binding.</summary>
public sealed record ShortFormCopy(
    IReadOnlyList<GroundedText> HookLines,
    GroundedText? SpecLine,
    GroundedText? AgentContact,
    IReadOnlyList<GroundedText> VoiceoverLines,
    IReadOnlyList<NarrationSegment>? KeptNarration,
    IReadOnlyList<FactBinding> AddedFactBindings);

public static class ShortFormCopyBuilder
{
    public const string CityStateKey = "property.cityState";
    public const string PriceBedBathKey = "property.priceBedBath";
    public const string AgentContactKey = "brand.agentContact";
    public const string JustListedKey = "campaign.justListed";
    public const string OpenHouseKey = "campaign.openHouse";
    public const string OpenHouseTimeKey = "campaign.openHouseTime";
    public const string PriceReductionKey = "campaign.priceReduction";

    private const int MaximumHookLines = 4;

    /// <summary>A relaxed read is about 2.2 words a second; the budget keeps the voice inside the video.</summary>
    private const decimal WordsPerSecond = 2.2m;

    public static RequestedDuration DurationFor(CampaignVoiceoverMode voiceover) =>
        voiceover == CampaignVoiceoverMode.Full ? RequestedDuration.Hero60 : RequestedDuration.Feature30;

    /// <param name="master">The master plan with its narration already in the spoken language.</param>
    /// <param name="shortVoiceover">
    /// When false, a short-hook voiceover keeps only the opening and closing lines (used to retry a voiceover that
    /// ran long in the spoken language).
    /// </param>
    public static ShortFormCopy Build(
        VideoProductionSpecification master,
        CampaignCreativeOptions options,
        string? city,
        string? state,
        bool shortVoiceover = true)
    {
        ArgumentNullException.ThrowIfNull(master);
        ArgumentNullException.ThrowIfNull(options);
        var bindings = master.FactBindings
            .GroupBy(binding => binding.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var added = new List<FactBinding>();
        GroundedText? Bound(string key) => bindings.TryGetValue(key, out var binding)
            && !string.IsNullOrWhiteSpace(binding.Value)
                ? new GroundedText(binding.Value, binding.Key)
                : null;
        GroundedText Add(string key, string value, FactSource source, string reference)
        {
            var binding = new FactBinding(key, value, source, reference);
            added.RemoveAll(existing => existing.Key == key);
            added.Add(binding);
            bindings[key] = binding;
            return new GroundedText(value, key);
        }

        var price = Bound("property.listingPrice");
        var bedBath = Bound("property.bedBath");
        GroundedText? cityState = null;
        if (!string.IsNullOrWhiteSpace(city))
        {
            cityState = Add(
                CityStateKey,
                string.IsNullOrWhiteSpace(state) ? city.Trim() : $"{city.Trim()}, {state.Trim()}",
                FactSource.VerifiedProperty,
                "City+State");
        }

        var hookLines = new List<GroundedText>();
        if (options.Elements.HasFlag(CampaignVideoElement.OpeningTitle))
        {
            GroundedText? title = options.MarketingTemplate switch
            {
                CampaignMarketingTemplate.JustListed => Add(
                    JustListedKey, "JUST LISTED", FactSource.AgentVerifiedCampaignInput, "Just Listed template"),
                CampaignMarketingTemplate.OpenHouse => Add(
                    OpenHouseKey, "OPEN HOUSE", FactSource.AgentVerifiedCampaignInput, "Open House template"),
                CampaignMarketingTemplate.PriceDrop when options.PriceReductionAmount is { } amount => Add(
                    PriceReductionKey,
                    $"PRICE REDUCED {amount.ToString("C0", CultureInfo.GetCultureInfo("en-US"))}",
                    FactSource.AgentVerifiedCampaignInput,
                    "Agent-confirmed campaign input"),
                _ => price,
            };
            var candidates = new List<GroundedText?> { title };
            if (options.MarketingTemplate == CampaignMarketingTemplate.OpenHouse
                && !string.IsNullOrWhiteSpace(options.OpenHouseText))
            {
                candidates.Add(Add(
                    OpenHouseTimeKey,
                    options.OpenHouseText.Trim(),
                    FactSource.AgentVerifiedCampaignInput,
                    "Agent-confirmed open house time"));
            }

            candidates.AddRange([price, cityState, bedBath]);
            hookLines.AddRange(candidates
                .OfType<GroundedText>()
                .DistinctBy(line => line.GroundingKey)
                .Take(MaximumHookLines));
        }

        GroundedText? specLine = null;
        if (options.Elements.HasFlag(CampaignVideoElement.PropertyFacts) && bedBath is not null)
        {
            specLine = price is null
                ? bedBath
                : Add(PriceBedBathKey, $"{price.Text} • {bedBath.Text}", FactSource.VerifiedProperty,
                    "ListingPrice+Bedrooms+Bathrooms");
        }

        GroundedText? agentContact = null;
        var contactParts = new[] { master.Brand.AgentName, master.Brand.Phone }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part!.Trim())
            .ToArray();
        if (options.Elements.HasFlag(CampaignVideoElement.AgentContact) && contactParts.Length > 0)
        {
            agentContact = Add(AgentContactKey, string.Join(" • ", contactParts), FactSource.BrandKit, "AgentName+Phone");
        }

        var voiceover = new List<GroundedText>();
        IReadOnlyList<NarrationSegment>? kept = null;
        switch (options.Voiceover)
        {
            case CampaignVoiceoverMode.Full:
                kept = master.Audio.NarrationSegments;
                break;
            case CampaignVoiceoverMode.ShortHook:
                voiceover.AddRange(ShortHook(bindings, (int)DurationFor(options.Voiceover), shortVoiceover));
                break;
        }

        return new ShortFormCopy(hookLines, specLine, agentContact, voiceover, kept, added);
    }

    /// <summary>The story's opening hook, as many highlights as fit, then its closing call to action.</summary>
    private static List<GroundedText> ShortHook(
        Dictionary<string, FactBinding> bindings,
        int durationSeconds,
        bool includeHighlights)
    {
        var budget = (int)((durationSeconds - 2) * WordsPerSecond);
        var opening = Line("story.openingHook");
        var closing = Line("story.closingCta");
        var lines = new List<GroundedText>();
        var used = Words(opening) + Words(closing);
        if (opening is not null)
        {
            lines.Add(opening);
        }

        if (includeHighlights)
        {
            for (var index = 1; bindings.ContainsKey($"story.highlight.{index}"); index++)
            {
                var highlight = Line($"story.highlight.{index}");
                if (highlight is null || used + Words(highlight) > budget)
                {
                    continue;
                }

                lines.Add(highlight);
                used += Words(highlight);
            }
        }

        if (closing is not null)
        {
            lines.Add(closing);
        }

        return lines;

        GroundedText? Line(string key) => bindings.TryGetValue(key, out var binding)
            && !string.IsNullOrWhiteSpace(binding.Value)
                ? new GroundedText(binding.Value, key)
                : null;
    }

    private static int Words(GroundedText? line) =>
        line?.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length ?? 0;
}
