using Bunit;
using ListingStudio.Application.Administration;
using ListingStudio.Domain.Administration;
using ListingStudio.Web.Components.Pages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using Xunit;

namespace ListingStudio.UnitTests;

/// <summary>Prompt 12: the release switch page for VerticalVideoLibraryV1.</summary>
public sealed class AdminFeaturesTests : IAsyncDisposable
{
    private static readonly FeatureWorkspaceChoice Testers = new(Guid.Parse("10000000-0000-0000-0000-000000000001"), "Tester Realty", 2, "Tester");
    private static readonly FeatureWorkspaceChoice Admins = new(Guid.Parse("10000000-0000-0000-0000-000000000002"), "Listing Studio HQ", 1, "Platform admin");
    private static readonly FeatureWorkspaceChoice Harbor = new(Guid.Parse("10000000-0000-0000-0000-000000000003"), "Harbor Homes", 4, null);
    private readonly BunitContext context = new();
    private readonly FakeRollout rollout = new();

    public AdminFeaturesTests()
    {
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton<IFeatureRolloutService>(rollout);
        context.Services.AddSingleton<ILogger<AdminFeatures>>(NullLogger<AdminFeatures>.Instance);
        context.AddAuthorization().SetAuthorized("admin").SetClaims(
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "admin-1"));
    }

    public ValueTask DisposeAsync() => context.DisposeAsync();

    [Fact]
    public void EveryReleaseUnitIsVisibleWithoutOpeningAPopover()
    {
        var cut = context.Render<AdminFeatures>();
        var picker = cut.Find("[data-testid=feature-picker]");

        Assert.Contains("Vertical video library", picker.TextContent, StringComparison.Ordinal);
        Assert.Contains("Kling 4 continuous Tour Map", picker.TextContent, StringComparison.Ordinal);
        Assert.Contains("Kling 4 native presenter", picker.TextContent, StringComparison.Ordinal);
        Assert.Equal(3, picker.QuerySelectorAll("button").Length);
    }

    [Fact]
    public void PilotForTestersAndAdminsSavesThroughTheCentralSwitch()
    {
        var cut = context.Render<AdminFeatures>();
        Assert.StartsWith("Off for everyone", cut.Find("[data-testid=feature-summary]").TextContent, StringComparison.Ordinal);
        Assert.True(cut.Find("[data-testid=feature-save] ").HasAttribute("disabled"));

        cut.Find("[data-testid=feature-add-suggested]").Click();
        Assert.Contains("Tester Realty", cut.Find("[data-testid=feature-pilots]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Listing Studio HQ", cut.Find("[data-testid=feature-pilots]").TextContent, StringComparison.Ordinal);
        cut.Find("input[data-testid=feature-global], [data-testid=feature-global] input").Change(true);
        Assert.Equal("After saving: On for 2 pilot workspaces.", cut.Find("[data-testid=feature-pending]").TextContent.Trim());

        // Another workspace can be found by name and added.
        cut.Find("[data-testid=feature-search]").Input("harb");
        cut.Find("[data-testid=feature-search-go]").Click();
        cut.FindAll(".feature-search button").Single(button => button.TextContent.Trim() == "Add").Click();
        Assert.Equal("After saving: On for 3 pilot workspaces.", cut.Find("[data-testid=feature-pending]").TextContent.Trim());

        if (Environment.GetEnvironmentVariable("ADMIN_FEATURES_MARKUP") is { Length: > 0 } markupPath)
        {
            File.WriteAllText(markupPath, cut.Markup);
        }

        cut.Find("[data-testid=feature-save]").Click();
        var saved = Assert.Single(rollout.Saves);
        Assert.Equal((true, false), (saved.Global, saved.Everyone));
        Assert.Equal(new[] { Testers.Id, Admins.Id, Harbor.Id }.Order(), saved.Pilots.Order());
        Assert.Contains("On for 3 pilot workspaces.", cut.Find("[data-testid=features-notice]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void TheKillSwitchKeepsTheEveryWorkspaceChoiceForWhenItIsTurnedBackOn()
    {
        rollout.State = new FeatureFlagState(FeatureFlagKeys.VerticalVideoLibraryV1, true, true, [Testers.Id], DateTimeOffset.UnixEpoch);
        rollout.PilotList = [Testers];
        var cut = context.Render<AdminFeatures>();
        Assert.Equal("On for every workspace.", cut.Find("[data-testid=feature-summary]").TextContent.Trim());

        cut.Find("input[data-testid=feature-global], [data-testid=feature-global] input").Change(false);
        Assert.StartsWith("After saving: Off for everyone", cut.Find("[data-testid=feature-pending]").TextContent.Trim(), StringComparison.Ordinal);
        cut.Find("[data-testid=feature-save]").Click();
        var saved = Assert.Single(rollout.Saves);
        Assert.Equal((false, true), (saved.Global, saved.Everyone));
        Assert.Equal([Testers.Id], saved.Pilots);
    }

    [Fact]
    public void ARefusedChangeShowsTheReasonAndSavesNothing()
    {
        rollout.Refuse = true;
        var cut = context.Render<AdminFeatures>();
        cut.Find("input[data-testid=feature-global], [data-testid=feature-global] input").Change(true);
        cut.Find("[data-testid=feature-save]").Click();
        Assert.Equal("Platform administration access is required.", cut.Find("[data-testid=features-error]").TextContent.Trim());
        Assert.Empty(rollout.Saves);
    }

    private sealed class FakeRollout : IFeatureRolloutService
    {
        public FeatureFlagState State { get; set; } = FeatureFlagState.Off(FeatureFlagKeys.VerticalVideoLibraryV1);

        public IReadOnlyList<FeatureWorkspaceChoice> PilotList { get; set; } = [];

        public bool Refuse { get; set; }

        public List<(bool Global, bool Everyone, Guid[] Pilots)> Saves { get; } = [];

        public Task<FeatureRolloutView> GetAsync(string userId, string featureKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(View());

        public Task<FeatureRolloutView> SaveAsync(string userId, string featureKey, bool globallyEnabled, bool enabledForAllOrganizations,
            IReadOnlyCollection<Guid> pilotOrganizationIds, CancellationToken cancellationToken = default)
        {
            if (Refuse)
            {
                throw new UnauthorizedAccessException();
            }

            Saves.Add((globallyEnabled, enabledForAllOrganizations, pilotOrganizationIds.ToArray()));
            State = new FeatureFlagState(featureKey, globallyEnabled, enabledForAllOrganizations, pilotOrganizationIds.ToArray(), DateTimeOffset.UnixEpoch);
            PilotList = new[] { Testers, Admins, Harbor }.Where(item => pilotOrganizationIds.Contains(item.Id)).ToArray();
            return Task.FromResult(View());
        }

        public Task<IReadOnlyList<FeatureWorkspaceChoice>> SearchWorkspacesAsync(string userId, string query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FeatureWorkspaceChoice>>(
                new[] { Harbor }.Where(item => item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray());

        private FeatureRolloutView View() => new(State, PilotList,
            new[] { Testers, Admins }.Where(item => !State.PilotOrganizationIds.Contains(item.Id)).ToArray());
    }
}
