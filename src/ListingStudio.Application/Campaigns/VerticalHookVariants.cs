using ListingStudio.Domain.Campaigns;

namespace ListingStudio.Application.Campaigns;

/// <summary>
/// Hook B for admin creative A/B tests: different opening words around the same grounded fact, or a fixed approved
/// template where the format's own hook can't be reworded safely. It never adds a claim the hook didn't already
/// make. Exclusive access has no hook B, because any other wording of an access line would be a new access claim.
/// </summary>
public static class VerticalHookVariants
{
    public const string A = CampaignVideoVariant.DefaultHookVariant;
    public const string B = "b";

    /// <summary>Whether the format has a hook B at all (it may still be unavailable for one listing).</summary>
    public static bool Supports(VerticalCreativeFormat format) => format is VerticalCreativeFormat.WowFeature
        or VerticalCreativeFormat.PriceFirst or VerticalCreativeFormat.HostedPov or VerticalCreativeFormat.StoryReveal;

    /// <summary>The recipe with hook B in place of hook A, on screen and in the captions; null when there is no distinct, safe hook B.</summary>
    public static VerticalCreativeRecipe? ApplyB(VerticalCreativeRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        var original = recipe.Facts.FirstOrDefault(fact => fact.Key == recipe.Hook.GroundingKey);
        var (text, reworded) = Alternate(recipe);
        if (original is null || text is null || text.Length > CampaignVideoVariant.MaximumHookLength
            || string.Equals(text, recipe.Hook.Text, StringComparison.Ordinal))
        {
            return null;
        }

        // Reworded hooks keep the original fact's source; fixed templates are approved template copy.
        var fact = reworded
            ? new VerticalRecipeFact($"{original.Key}.hookB", text, original.Source, $"{original.Reference} (hook B wording)")
            : new VerticalRecipeFact($"template.hookB.{recipe.Format}", text, VerticalCopySource.ApprovedTemplate, $"{recipe.RecipeVersion} hook B template");
        if (fact.Key.Length > CampaignVideoVariant.MaximumGroundingKeyLength || recipe.Facts.Any(existing => existing.Key == fact.Key))
        {
            return null;
        }

        var line = new VerticalRecipeLine(text, fact.Key);
        var updated = recipe with
        {
            Hook = line,
            Beats = recipe.Beats.Select(beat => beat.Overlay == recipe.Hook ? beat with { Overlay = line } : beat).ToArray(),
            Facts = [.. recipe.Facts, fact],
        };
        return updated.Validate().Count == 0 ? updated : null;
    }

    private static (string? Text, bool Reworded) Alternate(VerticalCreativeRecipe recipe)
    {
        var hook = recipe.Hook.Text.Trim();
        switch (recipe.Format)
        {
            case VerticalCreativeFormat.WowFeature:
                const string Look = "Look at this ";
                return hook.StartsWith(Look, StringComparison.Ordinal) && hook.Length > Look.Length + 1
                    ? ($"Wait until you see this {hook[Look.Length..]}", true)
                    : ("Wait for it.", false);
            case VerticalCreativeFormat.PriceFirst:
                var split = hook.IndexOf(" in ", StringComparison.Ordinal);
                return split > 0 && split + 4 < hook.Length
                    ? ($"What {hook[..split]} gets you in {hook[(split + 4)..]}", true)
                    : (null, false);
            case VerticalCreativeFormat.HostedPov:
                return ("Come take a look with me.", false);
            case VerticalCreativeFormat.StoryReveal:
                return ("Watch how this one turns out.", false);
            default:
                return (null, false);
        }
    }
}
