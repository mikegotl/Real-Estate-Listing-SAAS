using ListingStudio.Domain.Properties;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class PropertyMediaTests
{
    [Fact]
    public void CreateAssignsMetadataAndPendingAnalysis()
    {
        var organizationId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();

        var media = PropertyMedia.Create(
            organizationId,
            propertyId,
            "organizations/org/properties/property/photo.png",
            "photo.png",
            "image/png",
            1_024,
            1_920,
            1_080,
            3);

        Assert.Equal(organizationId, media.OrganizationId);
        Assert.Equal(propertyId, media.PropertyId);
        Assert.Equal(3, media.DisplayOrder);
        Assert.Equal(PropertyMediaAnalysisStatus.Pending, media.AnalysisStatus);
        Assert.NotEqual(Guid.Empty, media.Id);
    }

    [Fact]
    public void SetDisplayOrderRejectsNegativeValue()
    {
        var media = CreateMedia();

        Assert.Throws<ArgumentOutOfRangeException>(() => media.SetDisplayOrder(-1));
    }

    [Fact]
    public void CreateRejectsInvalidDimensions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PropertyMedia.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "image.png",
            "image.png",
            "image/png",
            100,
            0,
            100,
            0));
    }

    [Fact]
    public void AnalysisLifecyclePersistsValidatedStructuredResult()
    {
        var media = CreateMedia();
        var attemptedAt = DateTimeOffset.UtcNow;
        var completedAt = attemptedAt.AddSeconds(3);
        var analysis = new PropertyMediaAnalysis(
            PropertyMediaCategory.Kitchen,
            "Kitchen",
            92,
            88,
            false,
            true,
            false,
            ["Reflections in appliance surfaces"],
            "A bright kitchen with visible cabinetry and an island.",
            4);

        media.BeginAnalysis(attemptedAt);
        media.CompleteAnalysis(analysis, completedAt);

        Assert.Equal(PropertyMediaAnalysisStatus.Completed, media.AnalysisStatus);
        Assert.Equal(1, media.AnalysisAttemptCount);
        Assert.Equal(completedAt, media.AnalysisCompletedAtUtc);
        var persisted = media.GetAnalysis();
        Assert.NotNull(persisted);
        Assert.Equal(analysis.Category, persisted.Category);
        Assert.Equal(analysis.RoomType, persisted.RoomType);
        Assert.Equal(analysis.QualityScore, persisted.QualityScore);
        Assert.Equal(analysis.HeroScore, persisted.HeroScore);
        Assert.Equal(analysis.IsExterior, persisted.IsExterior);
        Assert.Equal(analysis.IsInterior, persisted.IsInterior);
        Assert.Equal(analysis.ContainsPeople, persisted.ContainsPeople);
        Assert.Equal(analysis.PotentialProblems, persisted.PotentialProblems);
        Assert.Equal(analysis.Description, persisted.Description);
        Assert.Equal(analysis.SuggestedDisplayOrder, persisted.SuggestedDisplayOrder);
    }

    [Fact]
    public void FailedAnalysisCanBeQueuedForExplicitRetry()
    {
        var media = CreateMedia();
        var attemptedAt = DateTimeOffset.UtcNow;
        media.BeginAnalysis(attemptedAt);
        media.FailAnalysis("Provider unavailable.", attemptedAt.AddMinutes(1));

        media.QueueAnalysisRetry();

        Assert.Equal(PropertyMediaAnalysisStatus.Pending, media.AnalysisStatus);
        Assert.Equal(0, media.AnalysisAttemptCount);
        Assert.Null(media.AnalysisLastError);
        Assert.Null(media.AnalysisNextAttemptAtUtc);
    }

    [Fact]
    public void CompleteAnalysisRejectsContradictoryInteriorExteriorFlags()
    {
        var media = CreateMedia();
        media.BeginAnalysis(DateTimeOffset.UtcNow);
        var analysis = new PropertyMediaAnalysis(
            PropertyMediaCategory.Other,
            "Unknown",
            50,
            50,
            true,
            true,
            false,
            [],
            "An ambiguous image.",
            0);

        Assert.Throws<ArgumentException>(() => media.CompleteAnalysis(analysis, DateTimeOffset.UtcNow));
    }

    private static PropertyMedia CreateMedia() => PropertyMedia.Create(
        Guid.NewGuid(),
        Guid.NewGuid(),
        "image.png",
        "image.png",
        "image/png",
        100,
        10,
        10,
        0);
}
