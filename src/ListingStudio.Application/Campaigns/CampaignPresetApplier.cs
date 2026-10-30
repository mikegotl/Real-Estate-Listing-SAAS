using ListingStudio.Application.Properties;
using ListingStudio.Domain.Campaigns;

namespace ListingStudio.Application.Campaigns;

/// <summary>What the property and the agent's profile have right now, which decides what part of a saved set applies.</summary>
public sealed record CampaignPresetContext(
    bool LongFormAvailable,
    bool HasPortrait,
    bool ProfileIncludesPresenter,
    bool PlanIncludesPresenter = true);

/// <param name="Settings">The choices to show on the Campaign tab.</param>
/// <param name="AgentAvatarConsentConfirmed">Whether the AI presenter consent starts ticked.</param>
/// <param name="Notes">One sentence for each choice that was skipped or still needs the agent.</param>
public sealed record CampaignPresetApplication(
    CampaignPresetSettings Settings,
    bool AgentAvatarConsentConfirmed,
    IReadOnlyList<string> Notes);

/// <summary>
/// Applies a saved set to one property: everything that works there is applied, anything that does not is left off
/// with a note, and nothing that must be confirmed per listing is ever filled in.
/// </summary>
public static class CampaignPresetApplier
{
    public static CampaignPresetApplication Apply(
        CampaignPresetSettings saved,
        IReadOnlyList<string> skippedValues,
        CampaignPresetContext context)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(skippedValues);
        ArgumentNullException.ThrowIfNull(context);
        var settings = saved.Normalize();
        var notes = new List<string>();
        if (skippedValues.Count > 0)
        {
            notes.Add($"Skipped saved choices this version no longer offers: {string.Join(", ", skippedValues)}.");
        }

        if (settings.Outputs.HasFlag(CampaignOutputSelection.FullNarratedTour) && !context.LongFormAvailable)
        {
            settings = settings with { Outputs = settings.Outputs & ~CampaignOutputSelection.FullNarratedTour };
            notes.Add(
                "Full narrated tour was left off: it needs an accepted script, or room-by-room narration, longer than "
                + $"{IPropertyNarrationScriptService.ShortFormWordLimit} words.");
        }

        if (settings.Outputs == CampaignOutputSelection.None)
        {
            notes.Add("Choose at least one deliverable before generating.");
        }

        const CampaignVideoElement agentCard = CampaignVideoElement.AgentPortrait | CampaignVideoElement.AgentAvatar;
        var consent = false;
        if ((settings.Elements & agentCard) != 0 && !context.HasPortrait)
        {
            settings = settings with { Elements = settings.Elements & ~agentCard };
            notes.Add("Your agent photo was left off because no profile headshot is uploaded.");
        }
        else if (settings.Elements.HasFlag(CampaignVideoElement.AgentAvatar) && !context.PlanIncludesPresenter)
        {
            // The still photo keeps the agent on the closing card when the plan has no AI presenter.
            settings = settings with
            {
                Elements = (settings.Elements & ~CampaignVideoElement.AgentAvatar) | CampaignVideoElement.AgentPortrait,
            };
            notes.Add("The AI agent presenter comes with Professional and Enterprise, so your agent photo is on the closing card instead.");
        }
        else if (settings.Elements.HasFlag(CampaignVideoElement.AgentAvatar))
        {
            // Only one agent card at a time; the presenter replaces the still photo.
            settings = settings with { Elements = settings.Elements & ~CampaignVideoElement.AgentPortrait };
            consent = context.ProfileIncludesPresenter;
            if (!consent)
            {
                notes.Add("Tick the AI presenter consent before generating.");
            }
        }

        settings = settings.Normalize();
        if (settings.Elements == CampaignVideoElement.None)
        {
            // A campaign needs at least one on-screen element or music, so fall back to the standard elements.
            settings = settings with
            {
                Elements = CampaignCreativeOptions.WithSoundtrack(CampaignPresetSettings.Default.Elements, settings.Soundtrack),
            };
        }

        if (settings.MarketingTemplate == CampaignMarketingTemplate.PriceDrop)
        {
            notes.Add("Enter this listing's price reduction and confirm it before generating.");
        }
        else if (settings.MarketingTemplate == CampaignMarketingTemplate.OpenHouse)
        {
            notes.Add("Enter this listing's open house day and time and confirm them before generating.");
        }

        return new CampaignPresetApplication(settings, consent, notes);
    }
}
