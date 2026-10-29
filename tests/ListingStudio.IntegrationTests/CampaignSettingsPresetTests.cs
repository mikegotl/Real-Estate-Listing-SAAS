using ListingStudio.Application.Authentication;
using ListingStudio.Application.Campaigns;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Organizations;
using ListingStudio.Infrastructure.Identity;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed class CampaignSettingsPresetTests(PostgreSqlWebApplicationFixture fixture)
    : IClassFixture<PostgreSqlWebApplicationFixture>
{
    private static readonly CampaignPresetSettings TikTokStyle = CampaignPresetSettings.Default with
    {
        Outputs = CampaignOutputSelection.TikTok,
        MarketingTemplate = CampaignMarketingTemplate.JustListed,
        Soundtrack = CampaignSoundtrack.AddInApp,
        Pace = CampaignPace.Cinematic,
        SpokenLanguage = CampaignLanguage.Spanish,
    };

    [Fact]
    public async Task SavedSettingsArePrivateUntilSharedAndNeverCrossWorkspaces()
    {
        var workspace = await CreateWorkspaceWithMemberAsync("presets-share");
        var outsider = await RegisterAsync("presets-outsider");
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ICampaignSettingsPresetService>();

        var saved = await service.SaveAsync(workspace.MemberId, "  My   TikTok style ", TikTokStyle);
        Assert.Equal("My TikTok style", saved.Name);
        Assert.Equal(TikTokStyle.Normalize(), saved.Settings);
        Assert.True(saved.IsOwn);
        Assert.True(saved.CanManage);
        Assert.False(saved.IsShared);

        var memberView = await service.ListAsync(workspace.MemberId);
        Assert.True(memberView.WorkspaceHasOtherMembers);
        Assert.Single(memberView.Items);
        Assert.Empty((await service.ListAsync(workspace.OwnerId)).Items);
        Assert.Null(await service.RenameAsync(workspace.OwnerId, saved.Id, "Taken over"));
        Assert.False(await service.DeleteAsync(workspace.OwnerId, saved.Id));

        var shared = await service.SetSharedAsync(workspace.MemberId, saved.Id, isShared: true);
        Assert.NotNull(shared);
        Assert.True(shared.IsShared);

        var ownerView = Assert.Single((await service.ListAsync(workspace.OwnerId)).Items);
        Assert.False(ownerView.IsOwn);
        Assert.False(ownerView.IsDefault);
        Assert.Equal(workspace.MemberEmail, ownerView.SharedBy);
        Assert.Equal(TikTokStyle.Normalize(), ownerView.Settings);
        // A workspace owner can tidy up shared sets, but cannot make someone else's set their own default.
        Assert.True(ownerView.CanManage);
        Assert.Null(await service.SetDefaultAsync(workspace.OwnerId, saved.Id, isDefault: true));

        var outsiderView = await service.ListAsync(outsider);
        Assert.Empty(outsiderView.Items);
        Assert.False(outsiderView.WorkspaceHasOtherMembers);
        Assert.Null(await service.UpdateSettingsAsync(outsider, saved.Id, CampaignPresetSettings.Default));
        Assert.Null(await service.SetSharedAsync(outsider, saved.Id, isShared: false));
        Assert.False(await service.DeleteAsync(outsider, saved.Id));

        var renamed = await service.RenameAsync(workspace.OwnerId, saved.Id, "Team TikTok");
        Assert.Equal("Team TikTok", renamed?.Name);
        Assert.Equal(workspace.MemberEmail, renamed?.SharedBy);

        var unshared = await service.SetSharedAsync(workspace.OwnerId, saved.Id, isShared: false);
        Assert.False(unshared?.IsShared);
        Assert.Empty((await service.ListAsync(workspace.OwnerId)).Items);
        Assert.Equal("Team TikTok", Assert.Single((await service.ListAsync(workspace.MemberId)).Items).Name);
    }

    [Fact]
    public async Task OwnerSettingsCanBeUpdatedRenamedMadeDefaultAndDeleted()
    {
        var owner = await RegisterAsync("presets-owner");
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ICampaignSettingsPresetService>();

        var first = await service.SaveAsync(owner, "Listing launch", TikTokStyle);
        var second = await service.SaveAsync(owner, "Open house weekend", TikTokStyle with
        {
            MarketingTemplate = CampaignMarketingTemplate.OpenHouse,
        });
        Assert.False((await service.ListAsync(owner)).WorkspaceHasOtherMembers);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveAsync(owner, "LISTING   launch", CampaignPresetSettings.Default));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RenameAsync(owner, second.Id, "listing launch"));

        var updated = await service.UpdateSettingsAsync(owner, first.Id, TikTokStyle with
        {
            Voiceover = CampaignVoiceoverMode.Full,
        });
        Assert.Equal(CampaignVoiceoverMode.Full, updated?.Settings.Voiceover);

        Assert.True((await service.SetDefaultAsync(owner, first.Id, isDefault: true))?.IsDefault);
        Assert.True((await service.SetDefaultAsync(owner, second.Id, isDefault: true))?.IsDefault);
        var list = await service.ListAsync(owner);
        Assert.Equal(second.Id, list.Default?.Id);
        Assert.Single(list.Items, item => item.IsDefault);

        Assert.False((await service.SetDefaultAsync(owner, second.Id, isDefault: false))?.IsDefault);
        Assert.Null((await service.ListAsync(owner)).Default);

        Assert.True(await service.DeleteAsync(owner, first.Id));
        Assert.Equal("Open house weekend", Assert.Single((await service.ListAsync(owner)).Items).Name);
    }

    [Fact]
    public async Task EachUserCanKeepALimitedNumberOfSavedSettings()
    {
        var owner = await RegisterAsync("presets-limit");
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ICampaignSettingsPresetService>();
        for (var index = 0; index < CampaignSettingsPreset.MaximumPresetsPerUser; index++)
        {
            await service.SaveAsync(owner, $"Style {index}", CampaignPresetSettings.Default);
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveAsync(owner, "One too many", CampaignPresetSettings.Default));
        Assert.Contains("Delete one to save another", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnreadableSavedSettingsAreLeftOutOfTheList()
    {
        var owner = await RegisterAsync("presets-corrupt");
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ICampaignSettingsPresetService>();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var good = await service.SaveAsync(owner, "Good", CampaignPresetSettings.Default);
        var bad = await service.SaveAsync(owner, "Bad", CampaignPresetSettings.Default);
        await dbContext.CampaignSettingsPresets
            .Where(preset => preset.Id == bad.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(preset => preset.SettingsJson, "not json"));

        Assert.Equal(good.Id, Assert.Single((await service.ListAsync(owner)).Items).Id);
    }

    private async Task<string> RegisterAsync(string prefix)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var registration = scope.ServiceProvider.GetRequiredService<IAccountRegistrationService>();
        var result = await registration.RegisterAsync(new RegisterAccountCommand(
            $"{prefix}-{Guid.NewGuid():N}@example.com",
            "Password123",
            $"{prefix} Realty {Guid.NewGuid():N}"));
        Assert.True(result.Succeeded, string.Join(", ", result.Errors));
        return result.UserId!;
    }

    private async Task<Workspace> CreateWorkspaceWithMemberAsync(string prefix)
    {
        var ownerId = await RegisterAsync(prefix);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var organizationId = await dbContext.OrganizationMembers
            .Where(member => member.UserId == ownerId)
            .Select(member => member.OrganizationId)
            .SingleAsync();
        var email = $"{prefix}-member-{Guid.NewGuid():N}@example.com";
        var member = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
        var created = await userManager.CreateAsync(member, "Password123");
        Assert.True(created.Succeeded, string.Join(", ", created.Errors.Select(error => error.Description)));
        dbContext.OrganizationMembers.Add(OrganizationMember.CreateMember(organizationId, member.Id));
        await dbContext.SaveChangesAsync();
        return new Workspace(ownerId, member.Id, email);
    }

    private sealed record Workspace(string OwnerId, string MemberId, string MemberEmail);
}
