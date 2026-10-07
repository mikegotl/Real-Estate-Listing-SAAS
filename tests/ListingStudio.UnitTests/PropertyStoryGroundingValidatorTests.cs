using ListingStudio.Application.Stories;
using ListingStudio.Domain.Properties;
using ListingStudio.Domain.Stories;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class PropertyStoryGroundingValidatorTests
{
    private readonly PropertyStoryGroundingValidator validator = new();

    [Fact]
    public void AcceptsExactVerifiedFactsAndConservativeMarketingLanguage()
    {
        var result = validator.Validate(Request, SafeStory);

        Assert.True(result.IsValid, string.Join(", ", result.Errors));
    }

    [Fact]
    public void RejectsUnsupportedNumbersAndProtectedPropertyClaims()
    {
        var unsupported = SafeStory with
        {
            PropertyNarrative = "This 4-bedroom home is near a top school and has an ocean view.",
        };

        var result = validator.Validate(Request, unsupported);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains('4'));
        Assert.Contains(result.Errors, error => error.Contains("school", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("ocean view", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MediaObservationDoesNotAuthorizeAPropertyClaim()
    {
        var request = Request with
        {
            MediaObservations =
            [
                Request.MediaObservations[0] with { Description = "An ocean view is visible through a window." },
            ],
        };
        var unsupported = SafeStory with { OpeningHook = "Wake up to an ocean view." };

        var result = validator.Validate(request, unsupported);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("ocean view", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExplainsHowToVerifyAClaimDetectedByPhotoAnalysis()
    {
        var request = Request with
        {
            MediaObservations =
            [
                Request.MediaObservations[0] with
                {
                    Description = "Roof-mounted solar panels are visible.",
                },
            ],
        };
        var unsupported = SafeStory with { OpeningHook = "A home with solar panels." };

        var result = validator.Validate(request, unsupported);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Contains("\"Solar\" was detected in photo analysis", error, StringComparison.Ordinal);
        Assert.Contains("add that fact to the Description", error, StringComparison.Ordinal);
        Assert.Contains("generate the story again", error, StringComparison.Ordinal);
        Assert.Contains("leave the Description unchanged", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptsAProtectedClaimWhenVerifiedInThePropertyDescription()
    {
        var request = Request with
        {
            VerifiedProperty = Request.VerifiedProperty with
            {
                Description = "A comfortable home with roof-mounted solar panels.",
            },
        };
        var verified = SafeStory with { OpeningHook = "A home with solar panels." };

        var result = validator.Validate(request, verified);

        Assert.True(result.IsValid, string.Join(", ", result.Errors));
    }

    [Fact]
    public void AcceptsApprovedNearbySchoolFactButRejectsSteeringLanguage()
    {
        var request = Request with
        {
            ApprovedNeighborhoodFacts =
            [
                new ApprovedNeighborhoodFact(
                    "School",
                    "Example Elementary School",
                    "10 Learning Lane, Raleigh, NC 27601",
                    1.2m,
                    "https://maps.google.test/example-school",
                    DateTimeOffset.UtcNow),
            ],
        };
        var factual = SafeStory with
        {
            PropertyNarrative = "Example Elementary School is 1.2 miles away by straight-line distance.",
        };

        var valid = validator.Validate(request, factual);
        Assert.True(valid.IsValid, string.Join(", ", valid.Errors));

        var steering = factual with
        {
            OpeningHook = "A family-friendly home near excellent schools.",
        };
        var invalid = validator.Validate(request, steering);
        Assert.False(invalid.IsValid);
        Assert.Contains(invalid.Errors, error => error.Contains("family-friendly", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(invalid.Errors, error => error.Contains("excellent schools", StringComparison.OrdinalIgnoreCase));
    }

    private static PropertyStoryGenerationRequest Request => new(
        new VerifiedPropertyData(
            "123 Main Street",
            null,
            "Raleigh",
            "NC",
            "27601",
            450_000m,
            3,
            2.5m,
            2_100,
            0.25m,
            1998,
            PropertyType.SingleFamily,
            "A comfortable home.",
            ListingStatus.Active),
        [
            new PropertyMediaObservation(
                Guid.NewGuid(),
                0,
                PropertyMediaCategory.FrontExterior,
                "Exterior",
                90,
                95,
                true,
                false,
                false,
                [],
                "Front exterior of a detached home.",
                0),
        ],
        new PropertyStoryBranding("Example Realty", null));

    private static PropertyStoryContent SafeStory => new(
        "Welcome to 123 Main Street",
        "A comfortable home in Raleigh.",
        "This 3-bedroom, 2.5-bath home offers 2,100 square feet and was built in 1998.",
        ["Listed at $450,000", "A 0.25-acre lot"],
        "Welcome to 123 Main Street. Explore a comfortable home in Raleigh.",
        "Contact Example Realty to learn more.",
        "Explore 123 Main Street, listed at $450,000.",
        "Discover 123 Main Street.");
}
