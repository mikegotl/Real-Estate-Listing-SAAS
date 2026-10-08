using ListingStudio.Application.Properties;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Properties;
using ListingStudio.Web.Components.Properties;
using ListingStudio.Web.Components.Workspace;
using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed class WorkspaceSummaryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CountsOnlyActiveListings()
    {
        var summary = WorkspaceSummary.From(
        [
            Summary("1 Ready St", photos: 5, campaign: CampaignGenerationStatus.Completed),
            Summary("2 Busy St", photos: 4, campaign: CampaignGenerationStatus.Running),
            Summary("3 Queue St", photos: 4, campaign: CampaignGenerationStatus.Queued),
            Summary("4 Empty St", photos: 0),
            Summary("5 Old St", photos: 0, archived: true, campaign: CampaignGenerationStatus.Completed),
        ]);

        Assert.Equal(4, summary.ActiveListings);
        Assert.Equal(1, summary.ListingsNeedingPhotos);
        Assert.Equal(2, summary.CampaignsInProgress);
        Assert.Equal(1, summary.CampaignsReady);
        Assert.DoesNotContain(summary.RecentlyUpdated, property => property.IsArchived);
    }

    [Fact]
    public void OrdersAttentionByUrgencyAndSkipsListingsThatNeedNoAction()
    {
        var summary = WorkspaceSummary.From(
        [
            Summary("Ready to generate", photos: 6),
            Summary("Few photos", photos: 1),
            Summary("No photos", photos: 0),
            Summary("Running", photos: 6, campaign: CampaignGenerationStatus.Running),
            Summary("Done", photos: 6, campaign: CampaignGenerationStatus.Completed),
            Summary("Blocked by analysis", photos: 6, failedAnalyses: 2, campaign: CampaignGenerationStatus.Running),
            Summary("Failed", photos: 6, campaign: CampaignGenerationStatus.Failed),
            Summary("Archived", photos: 0, archived: true),
        ]);

        Assert.Equal(
            ["Failed", "Blocked by analysis", "No photos", "Few photos", "Ready to generate"],
            summary.NeedsAttention.Select(item => item.Property.Address1));
        Assert.Equal(PropertyDetailsTab.Campaign, summary.NeedsAttention[0].NextStep.Tab);
        Assert.Equal(PropertyDetailsTab.Media, summary.NeedsAttention[1].NextStep.Tab);
        Assert.Equal("Add listing photos", summary.NeedsAttention[2].NextStep.Title);
    }

    [Fact]
    public void CapsAttentionAndRecentListsAndPrefersRecentlyUpdated()
    {
        var properties = Enumerable.Range(0, 8)
            .Select(index => Summary($"{index} Main St", photos: 0, updatedMinutesAgo: index))
            .ToList();

        var summary = WorkspaceSummary.From(properties);

        Assert.Equal(WorkspaceSummary.MaximumAttentionItems, summary.NeedsAttention.Count);
        Assert.Equal("0 Main St", summary.NeedsAttention[0].Property.Address1);
        Assert.Equal(["0 Main St", "1 Main St", "2 Main St"], summary.RecentlyUpdated.Select(property => property.Address1));
    }

    [Fact]
    public void EmptyWorkspaceHasNoAttentionItems()
    {
        var summary = WorkspaceSummary.From([]);

        Assert.Equal(0, summary.ActiveListings);
        Assert.Empty(summary.NeedsAttention);
        Assert.Empty(summary.RecentlyUpdated);
    }

    [Theory]
    [InlineData(null, "/properties/00000000-0000-0000-0000-000000000001")]
    [InlineData(PropertyDetailsTab.Media, "/properties/00000000-0000-0000-0000-000000000001?tab=media")]
    public void DetailsUrlLinksToTheRequestedTab(PropertyDetailsTab? tab, string expected)
    {
        Assert.Equal(expected, PropertyDisplay.DetailsUrl(new Guid("00000000-0000-0000-0000-000000000001"), tab));
    }

    private static PropertySummary Summary(
        string address,
        int photos,
        int failedAnalyses = 0,
        CampaignGenerationStatus? campaign = null,
        bool archived = false,
        int updatedMinutesAgo = 0) => new(
            Guid.NewGuid(),
            address,
            "Raleigh",
            "NC",
            "27601",
            450_000m,
            PropertyType.SingleFamily,
            ListingStatus.Active,
            archived,
            3,
            2m,
            1_800,
            Now.AddMinutes(-updatedMinutesAgo),
            photos,
            failedAnalyses,
            photos > 0 ? Guid.NewGuid() : null,
            campaign);
}
