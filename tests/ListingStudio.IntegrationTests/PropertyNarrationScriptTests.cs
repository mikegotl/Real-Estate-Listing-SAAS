using System.Text;
using ListingStudio.Application.Authentication;
using ListingStudio.Application.Properties;
using ListingStudio.Domain.Properties;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed class PropertyNarrationScriptTests(PostgreSqlWebApplicationFixture fixture)
    : IClassFixture<PostgreSqlWebApplicationFixture>
{
    [Fact]
    public async Task UploadIsPrivateAndInertUntilAcceptedAndReplacementRequiresFreshAcceptance()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var owner = await CreateOwnerAndPropertyAsync(scope.ServiceProvider, "script-owner");
        var outsider = await CreateOwnerAndPropertyAsync(scope.ServiceProvider, "script-outsider");
        var service = scope.ServiceProvider.GetRequiredService<IPropertyNarrationScriptService>();
        var initialText = "Welcome home. Follow the natural light into the kitchen and spacious living room.";

        var scriptId = await UploadTextAsync(service, owner, "tour.txt", initialText);

        var stored = await service.GetAsync(owner.UserId, owner.PropertyId);
        Assert.NotNull(stored);
        Assert.Equal(scriptId, stored.Id);
        Assert.Equal(initialText, stored.ExtractedText);
        Assert.False(stored.MarketingUseAccepted);
        Assert.Null(await service.GetAsync(outsider.UserId, owner.PropertyId));
        Assert.Null(await service.OpenReadAsync(outsider.UserId, scriptId));
        Assert.False(await service.SetMarketingUseAcceptedAsync(
            outsider.UserId, owner.PropertyId, scriptId, accepted: true));

        Assert.True(await service.SetMarketingUseAcceptedAsync(
            owner.UserId, owner.PropertyId, scriptId, accepted: true));
        Assert.True((await service.GetAsync(owner.UserId, owner.PropertyId))!.MarketingUseAccepted);

        var replacementText = "Begin at the entry. Continue through the dining room and finish in the private backyard.";
        var replacementId = await UploadTextAsync(service, owner, "replacement.md", replacementText, "text/markdown");
        var replaced = await service.GetAsync(owner.UserId, owner.PropertyId);
        Assert.NotNull(replaced);
        Assert.Equal(scriptId, replacementId);
        Assert.Equal(replacementText, replaced.ExtractedText);
        Assert.False(replaced.MarketingUseAccepted);
        Assert.Null(replaced.MarketingUseAcceptedAtUtc);

        var content = await service.OpenReadAsync(owner.UserId, scriptId);
        Assert.NotNull(content);
        await using (content.Content)
        using (var reader = new StreamReader(content.Content, Encoding.UTF8))
        {
            Assert.Equal(replacementText, await reader.ReadToEndAsync());
        }

        Assert.True(await service.DeleteAsync(owner.UserId, owner.PropertyId, scriptId));
        Assert.Null(await service.GetAsync(owner.UserId, owner.PropertyId));
    }

    [Fact]
    public async Task MarketingAcceptanceRejectsNarrationBeyondLongFormLimit()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var owner = await CreateOwnerAndPropertyAsync(scope.ServiceProvider, "script-length");
        var service = scope.ServiceProvider.GetRequiredService<IPropertyNarrationScriptService>();
        var text = string.Join(' ', Enumerable.Repeat("a", IPropertyNarrationScriptService.MaximumAcceptedWords + 1));
        var scriptId = await UploadTextAsync(service, owner, "long.txt", text);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SetMarketingUseAcceptedAsync(owner.UserId, owner.PropertyId, scriptId, accepted: true));

        Assert.Contains("supports up to", exception.Message, StringComparison.Ordinal);
        Assert.False((await service.GetAsync(owner.UserId, owner.PropertyId))!.MarketingUseAccepted);
    }

    [Fact]
    public async Task MarketingAcceptanceAllowsLongFormNarration()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var owner = await CreateOwnerAndPropertyAsync(scope.ServiceProvider, "script-long-form");
        var service = scope.ServiceProvider.GetRequiredService<IPropertyNarrationScriptService>();
        var text = string.Join(' ', Enumerable.Repeat("welcome", 1_000));
        var scriptId = await UploadTextAsync(service, owner, "long-form.txt", text);

        Assert.True(await service.SetMarketingUseAcceptedAsync(
            owner.UserId, owner.PropertyId, scriptId, accepted: true));
        var stored = await service.GetAsync(owner.UserId, owner.PropertyId);
        Assert.NotNull(stored);
        Assert.True(stored.MarketingUseAccepted);
        Assert.Equal(1_000, stored.WordCount);
        Assert.True(NarrationScriptPolicy.RequiresLongForm(stored.ExtractedText));
        Assert.Equal(420, NarrationScriptPolicy.EstimateLongFormDurationSeconds(stored.ExtractedText));
    }

    private static async Task<Guid> UploadTextAsync(
        IPropertyNarrationScriptService service,
        (string UserId, Guid PropertyId) owner,
        string filename,
        string text,
        string contentType = "text/plain")
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await using var content = new MemoryStream(bytes);
        return await service.UploadAsync(
            owner.UserId,
            owner.PropertyId,
            new PropertyNarrationScriptUpload(filename, contentType, bytes.Length, content));
    }

    private static async Task<(string UserId, Guid PropertyId)> CreateOwnerAndPropertyAsync(
        IServiceProvider services,
        string prefix)
    {
        var registration = services.GetRequiredService<IAccountRegistrationService>();
        var properties = services.GetRequiredService<IPropertyService>();
        var result = await registration.RegisterAsync(new RegisterAccountCommand(
            $"{prefix}-{Guid.NewGuid():N}@example.com",
            "Password123",
            $"{prefix} Realty {Guid.NewGuid():N}"));
        Assert.True(result.Succeeded, string.Join(", ", result.Errors));
        var propertyId = await properties.CreateAsync(result.UserId!, new PropertyInput(
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
            ListingStatus.Draft));
        return (result.UserId!, propertyId);
    }
}
