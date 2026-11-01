using Bunit;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Web.Components.Properties;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Xunit;

namespace ListingStudio.UnitTests;

/// <summary>The A to H caption look picker on the Campaign tab and in the Creative Testing Studio.</summary>
public sealed class CaptionStylePickerTests : IAsyncDisposable
{
    private readonly BunitContext context = new();

    public CaptionStylePickerTests()
    {
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
    }

    public ValueTask DisposeAsync() => context.DisposeAsync();

    [Fact]
    public void CollapsedItShowsTheChosenLookAndOpensToEveryLookAToH()
    {
        CampaignCaptionStyle? chosen = null;
        var cut = context.Render<CaptionStylePicker>(parameters => parameters
            .Add(picker => picker.Value, CampaignCaptionStyles.Default)
            .Add(picker => picker.ValueChanged, style => chosen = style));

        Assert.Equal("F · Neon night", cut.Find("[data-testid=caption-picker-current]").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-testid=caption-picker-grid]"));
        Assert.Equal("false", cut.Find("[data-testid=caption-picker-toggle]").GetAttribute("aria-expanded"));

        cut.Find("[data-testid=caption-picker-toggle]").Click();
        var cards = cut.FindAll("[role=radio]");
        Assert.Equal(["A", "B", "C", "D", "E", "F", "G", "H"],
            cards.Select(card => card.GetAttribute("data-testid")!["caption-style-".Length..]));
        Assert.Equal("true", cut.Find("[data-testid=caption-style-F]").GetAttribute("aria-checked"));
        Assert.All(cards, card => Assert.StartsWith("/images/caption-styles/", card.QuerySelector("img")!.GetAttribute("src"), StringComparison.Ordinal));

        cut.Find("[data-testid=caption-style-C]").Click();
        Assert.Equal(CampaignCaptionStyle.CandyPop, chosen);
        Assert.Empty(cut.FindAll("[data-testid=caption-picker-grid]"));
        Assert.Equal("C · Candy pop", cut.Find("[data-testid=caption-picker-current]").TextContent.Trim());
    }

    [Fact]
    public void ClassicCaptionsFromOlderCampaignsAreNamedAndEveryThumbnailExists()
    {
        var cut = context.Render<CaptionStylePicker>(parameters => parameters.Add(picker => picker.Value, CampaignCaptionStyle.Classic));
        Assert.Equal("Classic captions", cut.Find("[data-testid=caption-picker-current]").TextContent.Trim());

        Assert.Equal(CampaignCaptionStyles.Picker, CaptionStyleCatalog.Choices.Select(choice => choice.Style));
        var root = FindWebRoot();
        Assert.All(CaptionStyleCatalog.Choices, choice =>
            Assert.True(File.Exists(Path.Combine(root, choice.Thumbnail.TrimStart('/'))), choice.Thumbnail));
    }

    [Fact]
    public void ADisabledPickerCannotBeOpened()
    {
        var cut = context.Render<CaptionStylePicker>(parameters => parameters.Add(picker => picker.Disabled, true));
        Assert.True(cut.Find("[data-testid=caption-picker-toggle]").HasAttribute("disabled"));
    }

    private static string FindWebRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "ListingStudio.Web", "wwwroot");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("wwwroot");
    }
}
