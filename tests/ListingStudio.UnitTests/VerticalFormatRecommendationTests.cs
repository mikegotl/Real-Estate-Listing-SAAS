using ListingStudio.Application.Campaigns;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Properties;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class VerticalFormatRecommendationTests
{
    [Fact]
    public void TypicalPhotoListingGetsWowFeatureAndPriceFirstWithoutFillingTheThirdSlot()
    {
        var result = VerticalFormatRules.Evaluate(PhotoListing());

        Assert.Equal(
            [VerticalCreativeFormat.WowFeature, VerticalCreativeFormat.PriceFirst],
            result.Recommended.Select(item => item.Format));
        Assert.Equal(VerticalCampaignPurpose.Reach, result.For(VerticalCreativeFormat.WowFeature).Purpose);
        Assert.Equal(VerticalCampaignPurpose.Consideration, result.For(VerticalCreativeFormat.PriceFirst).Purpose);
        Assert.False(result.For(VerticalCreativeFormat.HostedPov).IsEligible);
        Assert.False(result.For(VerticalCreativeFormat.StoryReveal).IsEligible);
        Assert.False(result.For(VerticalCreativeFormat.ExclusiveAccess).IsEligible);
        Assert.Contains(result.For(VerticalCreativeFormat.WowFeature).Reasons, reason => reason.Contains("kitchen", StringComparison.Ordinal));
        Assert.All(result.Formats, item => Assert.InRange(item.Score, 0, 100));
    }

    [Fact]
    public void ListingWithoutPhotosHasNoRecommendationAndSaysWhatIsMissing()
    {
        var result = VerticalFormatRules.Evaluate(PhotoListing() with { Photos = [] });

        Assert.Empty(result.Recommended);
        Assert.Empty(result.AllEligible);
        Assert.Contains(result.For(VerticalCreativeFormat.WowFeature).MissingInputs, item => item.Contains("usable storyboard photos", StringComparison.Ordinal));
        Assert.Contains(result.For(VerticalCreativeFormat.PriceFirst).MissingInputs, item => item.Contains("usable storyboard photos", StringComparison.Ordinal));
        Assert.All(result.Formats, item => Assert.Equal(VerticalRecommendationStrength.NotEligible, item.Strength));
    }

    [Fact]
    public void PhotosStillWaitingForAnalysisKeepPriceFirstButNotWowFeature()
    {
        var pending = Enumerable.Range(0, 6)
            .Select(index => new VerticalPhotoInput(Id(index), null, false, null, null, 4032, 3024))
            .ToArray();

        var result = VerticalFormatRules.Evaluate(PhotoListing() with { Photos = pending });

        Assert.False(result.For(VerticalCreativeFormat.WowFeature).IsEligible);
        Assert.Contains(result.For(VerticalCreativeFormat.WowFeature).MissingInputs, item => item.Contains("analysis has not finished", StringComparison.Ordinal));
        Assert.True(result.For(VerticalCreativeFormat.PriceFirst).IsEligible);
    }

    [Fact]
    public void LowQualityPhotosDoNotCountAsUsable()
    {
        var blurry = Enumerable.Range(0, 8)
            .Select(index => Photo(index, PropertyMediaCategory.Kitchen, hero: 90, quality: 30))
            .ToArray();

        var result = VerticalFormatRules.Evaluate(PhotoListing() with { Photos = blurry });

        Assert.Equal(0, result.Inputs.UsablePhotos);
        Assert.Empty(result.AllEligible);
    }

    [Theory]
    [InlineData(PropertyType.SingleFamily)]
    [InlineData(PropertyType.Condominium)]
    [InlineData(PropertyType.Townhouse)]
    [InlineData(PropertyType.MultiFamily)]
    [InlineData(PropertyType.Other)]
    public void HomesWithCompleteFactsAlwaysSupportPriceFirst(PropertyType type)
    {
        var result = VerticalFormatRules.Evaluate(PhotoListing() with { PropertyType = type });

        var price = result.For(VerticalCreativeFormat.PriceFirst);
        Assert.True(price.IsEligible);
        Assert.Equal(75, price.Score);
        Assert.Equal(VerticalRecommendationStrength.Strong, price.Strength);
    }

    [Fact]
    public void LandNeedsALotSizeInsteadOfBedroomsAndBathrooms()
    {
        var land = PhotoListing() with
        {
            PropertyType = PropertyType.Land,
            Bedrooms = 0,
            Bathrooms = 0,
            SquareFeet = null,
            LotSize = null,
        };

        Assert.False(VerticalFormatRules.Evaluate(land).For(VerticalCreativeFormat.PriceFirst).IsEligible);
        var withLot = VerticalFormatRules.Evaluate(land with { LotSize = 2.5m }).For(VerticalCreativeFormat.PriceFirst);
        Assert.True(withLot.IsEligible);
        Assert.Contains("The lot size is entered.", withLot.Reasons);
    }

    [Fact]
    public void HomeWithoutBedroomsOrPriceCannotLeadWithPrice()
    {
        Assert.False(VerticalFormatRules.Evaluate(PhotoListing() with { Bedrooms = 0 })
            .For(VerticalCreativeFormat.PriceFirst).IsEligible);
        var noPrice = VerticalFormatRules.Evaluate(PhotoListing() with { ListingPrice = 0 })
            .For(VerticalCreativeFormat.PriceFirst);
        Assert.False(noPrice.IsEligible);
        Assert.Contains("The listing price.", noPrice.MissingInputs);
        Assert.False(VerticalFormatRules.Evaluate(PhotoListing() with { HasCityAndState = false })
            .For(VerticalCreativeFormat.PriceFirst).IsEligible);
    }

    [Fact]
    public void VerifiedValueMessageRaisesPriceFirstAndIsNeverAssumed()
    {
        var plain = VerticalFormatRules.Evaluate(PhotoListing()).For(VerticalCreativeFormat.PriceFirst);
        var verified = VerticalFormatRules.Evaluate(PhotoListing() with
        {
            Confirmations = new VerticalAgentConfirmations(ValuePositioning: true),
        }).For(VerticalCreativeFormat.PriceFirst);

        Assert.Equal(plain.Score + 15, verified.Score);
        Assert.Contains(verified.Reasons, reason => reason.Contains("verified price change", StringComparison.Ordinal));
        Assert.Contains(plain.MissingInputs, item => item.Contains("never inferred from market data", StringComparison.Ordinal));
    }

    [Fact]
    public void ApprovedNeighborhoodPlacesAddLocationContext()
    {
        var withPlaces = VerticalFormatRules.Evaluate(PhotoListing() with { ApprovedNeighborhoodPlaces = 5 })
            .For(VerticalCreativeFormat.PriceFirst);

        Assert.Equal(85, withPlaces.Score);
    }

    [Fact]
    public void PresenterPortraitWithoutConsentDoesNotCount()
    {
        var unconsented = VerticalFormatRules.Evaluate(PhotoListing() with
        {
            Presenter = new VerticalPresenterInput(HasPortrait: true, ConsentConfirmed: false),
        }).For(VerticalCreativeFormat.HostedPov);
        var consented = VerticalFormatRules.Evaluate(PhotoListing() with
        {
            Presenter = new VerticalPresenterInput(HasPortrait: true, ConsentConfirmed: true),
        }).For(VerticalCreativeFormat.HostedPov);
        var consentWithoutPortrait = VerticalFormatRules.Evaluate(PhotoListing() with
        {
            Presenter = new VerticalPresenterInput(HasPortrait: false, ConsentConfirmed: true),
        }).For(VerticalCreativeFormat.HostedPov);

        Assert.False(unconsented.IsEligible);
        Assert.Contains(unconsented.MissingInputs, item => item.Contains("consent to use it as a presenter was not confirmed", StringComparison.Ordinal));
        Assert.True(consented.IsEligible);
        Assert.Equal(unconsented.Score + 25, consented.Score);
        Assert.False(consentWithoutPortrait.IsEligible);
    }

    [Fact]
    public void FootageAndApprovedNarrationMakeAStrongHostedPov()
    {
        var result = VerticalFormatRules.Evaluate(PhotoListing() with
        {
            Footage = [new VerticalFootageInput(Id(100), 50_000, true)],
            HasApprovedNarration = true,
        });

        var hosted = result.For(VerticalCreativeFormat.HostedPov);
        Assert.True(hosted.IsEligible);
        Assert.Equal(75, hosted.Score);
        Assert.Equal(VerticalCampaignPurpose.LeadGeneration, hosted.Purpose);
        Assert.Equal(3, result.Recommended.Count);
    }

    [Fact]
    public void FootageStillProcessingIsNotCounted()
    {
        var result = VerticalFormatRules.Evaluate(PhotoListing() with
        {
            Footage = [new VerticalFootageInput(Id(100), 50_000, false)],
        });

        Assert.Equal(0, result.Inputs.ReadyFootageSeconds);
        Assert.False(result.For(VerticalCreativeFormat.HostedPov).IsEligible);
    }

    [Fact]
    public void ApprovedNarrationAloneIsEligibleButTooWeakForTheDefaultSet()
    {
        var result = VerticalFormatRules.Evaluate(PhotoListing() with { HasApprovedNarration = true });

        var hosted = result.For(VerticalCreativeFormat.HostedPov);
        Assert.True(hosted.IsEligible);
        Assert.Equal(VerticalRecommendationStrength.Weak, hosted.Strength);
        Assert.False(hosted.IsRecommended);
        Assert.Contains(hosted, result.AllEligible);
        Assert.DoesNotContain(hosted, result.Recommended);
        Assert.Contains(hosted.Reasons, reason => reason.Contains("Generate all eligible formats", StringComparison.Ordinal));
    }

    [Fact]
    public void UnconfirmedRenovationClaimLeavesStoryRevealIneligible()
    {
        var mentions = VerticalUnconfirmedMentions.Find(["Fully renovated in 2024 with a remodeled kitchen."]);
        var result = VerticalFormatRules.Evaluate(PhotoListing() with
        {
            Mentions = mentions,
            Photos = [.. PhotoListing().Photos,
                Photo(20, PropertyMediaCategory.Kitchen, 80, 80, VerticalPhotoStoryRole.Before),
                Photo(21, PropertyMediaCategory.Kitchen, 80, 80, VerticalPhotoStoryRole.After)],
        });

        var story = result.For(VerticalCreativeFormat.StoryReveal);
        Assert.True(mentions.Renovation);
        Assert.False(story.IsEligible);
        Assert.False(story.IsRecommended);
        Assert.Contains(story.MissingInputs, item => item.Contains("no agent has confirmed it", StringComparison.Ordinal));
    }

    [Fact]
    public void StoryRevealNeedsBothTheConfirmedProblemAndPayoffAndPrefersBeforeAndAfter()
    {
        var problemOnly = PhotoListing() with { Confirmations = new VerticalAgentConfirmations(StoryProblem: true) };
        Assert.False(VerticalFormatRules.Evaluate(problemOnly).For(VerticalCreativeFormat.StoryReveal).IsEligible);

        var confirmed = PhotoListing() with
        {
            Confirmations = new VerticalAgentConfirmations(StoryProblem: true, StoryPayoff: true),
        };
        var without = VerticalFormatRules.Evaluate(confirmed).For(VerticalCreativeFormat.StoryReveal);
        var with = VerticalFormatRules.Evaluate(confirmed with
        {
            Photos = [.. confirmed.Photos,
                Photo(20, PropertyMediaCategory.Kitchen, 80, 80, VerticalPhotoStoryRole.Before),
                Photo(21, PropertyMediaCategory.Kitchen, 80, 80, VerticalPhotoStoryRole.After)],
        }).For(VerticalCreativeFormat.StoryReveal);

        Assert.True(without.IsEligible);
        Assert.Contains("Before and after photos (preferred).", without.MissingInputs);
        Assert.True(with.IsEligible);
        Assert.True(with.Score >= without.Score + 30);
    }

    [Fact]
    public void UnconfirmedExclusivityClaimLeavesExclusiveAccessIneligibleEvenWithFootage()
    {
        var mentions = VerticalUnconfirmedMentions.Find(["Exclusive off-market private preview before it hits the MLS."]);
        var withFootage = PhotoListing() with
        {
            Mentions = mentions,
            Footage = [new VerticalFootageInput(Id(100), 40_000, true)],
        };

        var access = VerticalFormatRules.Evaluate(withFootage).For(VerticalCreativeFormat.ExclusiveAccess);
        Assert.True(mentions.Exclusivity);
        Assert.False(access.IsEligible);
        Assert.Contains(access.MissingInputs, item => item.Contains("no agent has confirmed it", StringComparison.Ordinal));

        var confirmed = VerticalFormatRules.Evaluate(withFootage with
        {
            Confirmations = new VerticalAgentConfirmations(AccessOrTiming: true),
        }).For(VerticalCreativeFormat.ExclusiveAccess);
        Assert.True(confirmed.IsEligible);

        var confirmedWithoutFootage = VerticalFormatRules.Evaluate(PhotoListing() with
        {
            Confirmations = new VerticalAgentConfirmations(AccessOrTiming: true),
        }).For(VerticalCreativeFormat.ExclusiveAccess);
        Assert.False(confirmedWithoutFootage.IsEligible);
    }

    [Fact]
    public void EqualScoresAreBrokenByTheFixedFormatOrder()
    {
        var result = VerticalFormatRules.Evaluate(PhotoListing() with
        {
            Photos = PlainPhotos(),
            Footage = [new VerticalFootageInput(Id(100), 45_000, true)],
            HasApprovedNarration = true,
        });

        var price = result.For(VerticalCreativeFormat.PriceFirst);
        var hosted = result.For(VerticalCreativeFormat.HostedPov);
        Assert.Equal(price.Score, hosted.Score);
        Assert.True(price.Rank < hosted.Rank);
        Assert.True(result.For(VerticalCreativeFormat.StoryReveal).Rank
            < result.For(VerticalCreativeFormat.ExclusiveAccess).Rank
            || result.For(VerticalCreativeFormat.StoryReveal).Score
            != result.For(VerticalCreativeFormat.ExclusiveAccess).Score);
    }

    [Fact]
    public void DefaultSetSwapsInAReachVideoWhenTheTopThreeHaveNone()
    {
        var result = VerticalFormatRules.Evaluate(PhotoListing() with
        {
            Photos = [.. PlainPhotos(), Photo(30, PropertyMediaCategory.Kitchen, 70, 60),
                Photo(31, PropertyMediaCategory.Bedroom, 40, 60, VerticalPhotoStoryRole.Before),
                Photo(32, PropertyMediaCategory.Bedroom, 40, 60, VerticalPhotoStoryRole.After)],
            Footage = [new VerticalFootageInput(Id(100), 60_000, true)],
            HasApprovedNarration = true,
            Presenter = new VerticalPresenterInput(true, true),
            Confirmations = new VerticalAgentConfirmations(true, true, true),
        });

        var wow = result.For(VerticalCreativeFormat.WowFeature);
        Assert.True(wow.IsEligible);
        Assert.True(wow.Score >= VerticalFormatRules.RecommendThreshold);
        Assert.True(wow.Rank > 3);
        Assert.True(wow.IsRecommended);
        Assert.Contains(wow.Reasons, reason => reason.Contains("reach video", StringComparison.Ordinal));
        Assert.Equal(3, result.Recommended.Count);
        Assert.Contains(result.Recommended, item => item.Purpose == VerticalCampaignPurpose.Reach);
        Assert.Contains(result.Recommended, item => item.Purpose is VerticalCampaignPurpose.Consideration or VerticalCampaignPurpose.LeadGeneration);
    }

    [Fact]
    public void OutputIsDeterministicWhateverTheInputOrder()
    {
        var inputs = PhotoListing() with { Footage = [new VerticalFootageInput(Id(100), 20_000, true)] };
        var shuffled = inputs with { Photos = inputs.Photos.Reverse().ToArray() };

        var first = VerticalFormatRules.Evaluate(inputs).ToSnapshotJson();
        Assert.Equal(first, VerticalFormatRules.Evaluate(inputs).ToSnapshotJson());
        Assert.Equal(first, VerticalFormatRules.Evaluate(shuffled).ToSnapshotJson());
    }

    [Fact]
    public void EveryResultCarriesTheStableRuleVersionAndTheSnapshotRoundTrips()
    {
        var result = VerticalFormatRules.Evaluate(PhotoListing());

        Assert.Equal("vertical-rules-v1", VerticalFormatRules.Version);
        Assert.Equal(VerticalFormatRules.Version, result.RuleVersion);
        Assert.All(result.Formats, item => Assert.Equal(VerticalFormatRules.Version, item.RuleVersion));
        Assert.Equal(VerticalFormatRules.Formats.Count, result.Formats.Count);
        Assert.Equal(Enumerable.Range(1, result.Formats.Count), result.Formats.Select(item => item.Rank));

        var json = result.ToSnapshotJson();
        Assert.True(json.Length < 200_000);
        Assert.Contains("\"WowFeature\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"recommended\"", json, StringComparison.Ordinal);
        var restored = VerticalFormatRecommendationSet.FromSnapshotJson(json);
        Assert.NotNull(restored);
        Assert.Equal(json, restored.ToSnapshotJson());
        Assert.Equal(result.Recommended.Select(item => item.Format), restored.Recommended.Select(item => item.Format));
        Assert.Null(VerticalFormatRecommendationSet.FromSnapshotJson("not json"));
        Assert.Null(VerticalFormatRecommendationSet.FromSnapshotJson(null));
    }

    [Fact]
    public void SelectionChoosesTheDefaultSetAllEligibleOrNothing()
    {
        var result = VerticalFormatRules.Evaluate(PhotoListing() with { HasApprovedNarration = true });

        Assert.Empty(result.Select(VerticalVideoSelection.LegacySingle));
        Assert.Equal(2, result.Select(VerticalVideoSelection.RecommendedThree).Count);
        Assert.Equal(3, result.Select(VerticalVideoSelection.AllEligible).Count);
    }

    [Fact]
    public void MentionsAreFoundOnlyForClaimWords()
    {
        Assert.Equal(VerticalUnconfirmedMentions.None, VerticalUnconfirmedMentions.Find(["Three bedrooms near the park.", null, " "]));
        Assert.True(VerticalUnconfirmedMentions.Find(["Newly UPDATED baths"]).Renovation);
        Assert.True(VerticalUnconfirmedMentions.Find(["Coming soon: book a first look"]).Exclusivity);
    }

    [Fact]
    public void ContextUsesConsentAndVerifiedPriceChangeFromTheCampaignRequest()
    {
        var portrait = Guid.NewGuid();
        var options = CampaignCreativeOptions.SocialDefault with
        {
            AgentPortraitId = portrait,
            AgentAvatarConsentConfirmed = true,
            PriceReductionAmount = 10_000,
            PriceReductionVerified = true,
        };

        var context = VerticalRecommendationContext.From(options);
        Assert.Equal(new VerticalRecommendationContext(portrait, true, true), context);
        Assert.Equal(
            new VerticalRecommendationContext(null, false, false),
            VerticalRecommendationContext.From(CampaignCreativeOptions.SocialDefault with
            {
                AgentAvatarConsentConfirmed = true,
                PriceReductionAmount = 10_000,
            }));
    }

    private static VerticalFormatInputs PhotoListing() => new(
        PropertyType.SingleFamily,
        450_000m,
        3,
        2m,
        1_800,
        null,
        true,
        0,
        [
            Photo(0, PropertyMediaCategory.FrontExterior, 65, 75),
            Photo(1, PropertyMediaCategory.Entry, 50, 70),
            Photo(2, PropertyMediaCategory.Kitchen, 88, 85),
            Photo(3, PropertyMediaCategory.LivingRoom, 72, 80),
            Photo(4, PropertyMediaCategory.DiningRoom, 55, 70),
            Photo(5, PropertyMediaCategory.PrimaryBedroom, 50, 72),
            Photo(6, PropertyMediaCategory.Bathroom, 40, 68),
            Photo(7, PropertyMediaCategory.Backyard, 66, 74),
        ],
        [],
        false,
        VerticalPresenterInput.None,
        VerticalAgentConfirmations.None,
        VerticalUnconfirmedMentions.None);

    /// <summary>Eight usable photos with no standout feature: hero scores below the Wow Feature bar.</summary>
    private static VerticalPhotoInput[] PlainPhotos() => Enumerable.Range(0, 8)
        .Select(index => Photo(index, PropertyMediaCategory.Bedroom, 40, 60))
        .ToArray();

    private static VerticalPhotoInput Photo(
        int index,
        PropertyMediaCategory category,
        int hero,
        int quality,
        VerticalPhotoStoryRole role = VerticalPhotoStoryRole.None) =>
        new(Id(index), category, true, quality, hero, 4032, 3024, role);

    private static Guid Id(int index) => new($"00000000-0000-0000-0000-{index + 1:000000000000}");
}
