using System.IO.Compression;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication;
using System.Text.Json;
using ListingStudio.Application.Campaigns;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ListingStudio.IntegrationTests;

/// <summary>Prompt 8: each library video is reviewed, revised and downloaded on its own, by deliverable id and version.</summary>
public sealed partial class CampaignGenerationTests
{
    private static readonly JsonSerializerOptions ManifestReadOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task LibraryVideosAreReviewedRevisedAndDownloadedOneAtATime()
    {
        var (admin, owner) = await CreateLibraryListingAsync("library-review");
        try
        {
            await SetLibrarySwitchAsync(admin, true, true);
            QueueLibraryProviders();
            var queued = await EnqueueLibraryAsync(owner, VerticalVideoSelection.AllEligible);
            await ProcessUntilAsync(owner, queued.Id, CampaignGenerationStatus.Completed);

            CampaignVideoLibraryView view;
            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                var library = scope.ServiceProvider.GetRequiredService<ICampaignVideoLibraryService>();
                view = (await library.GetAsync(owner.UserId, queued.Id))!;
                Assert.Equal(VerticalVideoSelection.AllEligible, view.Selection);
                Assert.True(view.Videos.Count >= 2);
                Assert.All(view.Videos, video =>
                {
                    Assert.True(video.IsRendered);
                    Assert.Equal(CampaignVideoTechnicalStatus.Passed, video.TechnicalStatus);
                    Assert.Equal(1, video.Version);
                    Assert.NotNull(video.Score);
                    Assert.NotEmpty(video.Reasons);
                    Assert.False(string.IsNullOrWhiteSpace(video.Hook));
                    Assert.NotNull(video.Captions);
                    Assert.Null(video.Review.CurrentDecision);
                });
            }

            var first = view.Videos[0];
            var second = view.Videos[1];

            // Each video gets its own decision; a stale version is refused.
            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                var library = scope.ServiceProvider.GetRequiredService<ICampaignVideoLibraryService>();
                var approved = await library.DecideAsync(owner.UserId, first.DeliverableId!.Value, 1, CampaignVideoReviewDecision.Approved, null);
                Assert.Equal(CampaignVideoReviewDecision.Approved, approved!.CurrentDecision);
                await library.DecideAsync(owner.UserId, second.DeliverableId!.Value, 1, CampaignVideoReviewDecision.ChangesRequested,
                    "Use the upbeat track");
                await Assert.ThrowsAsync<InvalidOperationException>(() => library.DecideAsync(
                    owner.UserId, second.DeliverableId!.Value, 2, CampaignVideoReviewDecision.Approved, null));
                await Assert.ThrowsAsync<ArgumentException>(() => library.DecideAsync(
                    owner.UserId, second.DeliverableId!.Value, 1, CampaignVideoReviewDecision.ChangesRequested, "  "));
            }

            // The second video is revised through its own chat; only it gets a new version.
            fixture.Factory.VideoChatAssistant.Enqueue(request =>
            {
                Assert.Contains(VerticalFormatNames.Name(second.Format), request.VideoName, StringComparison.Ordinal);
                Assert.Equal("9:16", request.AspectRatio);
                return new CampaignVideoChatReply(
                    "I'll switch to the upbeat track.",
                    [new CampaignVideoEdit(CampaignVideoEditKind.ChangeMusic, Music: CampaignSoundtrack.UpbeatLifestyle)]);
            });
            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                var chat = scope.ServiceProvider.GetRequiredService<ICampaignVideoChatService>();
                var sent = (await chat.SendForVideoAsync(owner.UserId, second.DeliverableId!.Value, "Use the upbeat track"))!;
                Assert.Equal(second.DeliverableId, sent.DeliverableId);
                await chat.RenderForVideoAsync(owner.UserId, second.DeliverableId!.Value, sent.Messages[^1].Id);
                Assert.Empty((await chat.GetForVideoAsync(owner.UserId, first.DeliverableId!.Value))!.Messages);
            }

            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                var run = await scope.ServiceProvider.GetRequiredService<ICampaignGenerationProcessor>().ProcessNextRevisionAsync();
                Assert.NotNull(run);
                Assert.True(run.Succeeded, run.Error);
            }

            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                var library = scope.ServiceProvider.GetRequiredService<ICampaignVideoLibraryService>();
                var after = (await library.GetAsync(owner.UserId, queued.Id))!;
                var revised = after.Videos.Single(video => video.VariantId == second.VariantId);
                var untouched = after.Videos.Single(video => video.VariantId == first.VariantId);
                Assert.Equal(2, revised.Version);
                Assert.Null(revised.Review.CurrentDecision);
                Assert.Contains(revised.Review.History, entry => entry.Version == 1
                    && entry.Decision == CampaignVideoReviewDecision.ChangesRequested);
                Assert.Equal(1, untouched.Version);
                Assert.Equal(CampaignVideoReviewDecision.Approved, untouched.Review.CurrentDecision);

                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var revisedRow = await db.CampaignDeliverables.AsNoTracking().SingleAsync(item => item.Id == second.DeliverableId);
                Assert.Contains($"/variants/{second.VariantId:N}/v2/", revisedRow.AssetPath, StringComparison.Ordinal);

                // Links carry the version: the old cut is gone, the new one plays.
                Assert.Null(await library.OpenVideoAsync(owner.UserId, second.DeliverableId!.Value, 1));
                var current = await library.OpenVideoAsync(owner.UserId, second.DeliverableId!.Value, 2);
                Assert.NotNull(current);
                Assert.EndsWith("-v2.mp4", current.FileName, StringComparison.Ordinal);
                await current.Content.DisposeAsync();

                var caption = (await library.OpenCaptionAsync(owner.UserId, first.DeliverableId!.Value))!;
                using (var reader = new StreamReader(caption.Content))
                {
                    var text = await reader.ReadToEndAsync();
                    Assert.Contains(first.Captions!.ShortCaption, text, StringComparison.Ordinal);
                }

                // The ZIP holds each video and caption once, plus a manifest that matches them and leaks nothing.
                var archive = (await library.OpenArchiveAsync(owner.UserId, queued.Id))!;
                using var zipped = new MemoryStream();
                await archive.WriteAsync(zipped, CancellationToken.None);
                zipped.Position = 0;
                using var zip = new ZipArchive(zipped);
                Assert.Equal(after.Videos.Count, zip.Entries.Count(entry => entry.Name.EndsWith(".mp4", StringComparison.Ordinal)));
                Assert.Equal(after.Videos.Count, zip.Entries.Count(entry => entry.Name.EndsWith("-caption.txt", StringComparison.Ordinal)));
                string manifestText;
                await using (var manifestStream = await zip.GetEntry("manifest.json")!.OpenAsync())
                using (var manifestReader = new StreamReader(manifestStream))
                {
                    manifestText = await manifestReader.ReadToEndAsync();
                }

                var manifest = JsonSerializer.Deserialize<CampaignLibraryManifest>(manifestText, ManifestReadOptions)!;
                Assert.Equal(CampaignLibraryManifest.CurrentVersion, manifest.ManifestVersion);
                Assert.Equal(after.Videos.Count, manifest.Videos.Count);
                Assert.All(manifest.Videos, entry =>
                {
                    Assert.NotNull(zip.GetEntry(entry.FileName));
                    Assert.NotNull(zip.GetEntry(entry.CaptionFileName));
                    Assert.Equal(["TikTok", "Instagram Reels", "YouTube Shorts"], entry.IntendedPlatforms);
                    Assert.Equal("passed", entry.TechnicalValidation);
                    Assert.False(string.IsNullOrWhiteSpace(entry.Hook));
                    Assert.False(string.IsNullOrWhiteSpace(entry.CallToAction));
                });
                Assert.Contains(manifest.Videos, entry => entry.Version == 2 && entry.EditorialDecision == "NotReviewed");
                Assert.Contains(manifest.Videos, entry => entry.Version == 1 && entry.EditorialDecision == "Approved");
                Assert.DoesNotContain("http", manifestText, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("organizations/", manifestText, StringComparison.Ordinal);
                Assert.DoesNotContain(owner.OrganizationId.ToString("N"), manifestText, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(owner.OrganizationId.ToString(), manifestText, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("sig=", manifestText, StringComparison.Ordinal);

                // The campaign's legacy one-per-kind result list still holds no library video.
                var campaign = await scope.ServiceProvider.GetRequiredService<ICampaignGenerationService>().GetAsync(owner.UserId, queued.Id);
                Assert.DoesNotContain(campaign!.Deliverables, item => item.Kind == CampaignOutputKind.TikTok);
            }

            // Another workspace sees none of it.
            var stranger = await CreateOwnerAndPropertyAsync("library-review-stranger");
            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                var library = scope.ServiceProvider.GetRequiredService<ICampaignVideoLibraryService>();
                Assert.Null(await library.GetAsync(stranger.UserId, queued.Id));
                Assert.Null(await library.OpenVideoAsync(stranger.UserId, first.DeliverableId!.Value, null));
                Assert.Null(await library.OpenCaptionAsync(stranger.UserId, first.DeliverableId!.Value));
                Assert.Null(await library.OpenArchiveAsync(stranger.UserId, queued.Id));
                Assert.Null(await library.DecideAsync(stranger.UserId, first.DeliverableId!.Value, 1, CampaignVideoReviewDecision.Approved, null));
                var chat = scope.ServiceProvider.GetRequiredService<ICampaignVideoChatService>();
                Assert.Null(await chat.GetForVideoAsync(stranger.UserId, first.DeliverableId!.Value));
                Assert.Null(await chat.UndoForVideoAsync(stranger.UserId, second.DeliverableId!.Value));
            }

            // Undo puts the revised video back without touching the other one.
            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                var chat = scope.ServiceProvider.GetRequiredService<ICampaignVideoChatService>();
                var undone = (await chat.UndoForVideoAsync(owner.UserId, second.DeliverableId!.Value))!;
                // Undo restores version 1 itself, so that version's own review applies again.
                Assert.Equal(1, undone.Version);
                var library = scope.ServiceProvider.GetRequiredService<ICampaignVideoLibraryService>();
                var after = (await library.GetAsync(owner.UserId, queued.Id))!;
                Assert.Equal(CampaignVideoReviewDecision.Approved, after.Videos.Single(video => video.VariantId == first.VariantId).Review.CurrentDecision);
                Assert.Equal(CampaignVideoReviewDecision.ChangesRequested,
                    after.Videos.Single(video => video.VariantId == second.VariantId).Review.CurrentDecision);
            }
        }
        finally
        {
            await SetLibrarySwitchAsync(admin, false, false);
        }
    }

    [Fact]
    public async Task LibraryRoutesServeOnlyTheCurrentVersionToTheWorkspace()
    {
        var (admin, owner) = await CreateLibraryListingAsync("library-routes");
        try
        {
            await SetLibrarySwitchAsync(admin, true, true);
            QueueLibraryProviders();
            var queued = await EnqueueLibraryAsync(owner, VerticalVideoSelection.RecommendedThree);
            await ProcessUntilAsync(owner, queued.Id, CampaignGenerationStatus.Completed);
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var view = (await scope.ServiceProvider.GetRequiredService<ICampaignVideoLibraryService>().GetAsync(owner.UserId, queued.Id))!;
            var video = view.Videos.First(item => item.IsRendered);

            await using var web = fixture.CreateFactory(services =>
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = HeaderAuthenticationHandler.SchemeName;
                        options.DefaultChallengeScheme = HeaderAuthenticationHandler.SchemeName;
                    })
                    .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(HeaderAuthenticationHandler.SchemeName, _ => { }));
            using var client = web.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(HeaderAuthenticationHandler.SchemeName, owner.UserId);
            using var current = await client.GetAsync($"/campaigns/videos/{video.DeliverableId}?v=1&download=true");
            Assert.Equal(System.Net.HttpStatusCode.OK, current.StatusCode);
            Assert.Equal("video/mp4", current.Content.Headers.ContentType?.MediaType);
            Assert.Contains("-v1.mp4", current.Content.Headers.ContentDisposition?.FileNameStar ?? current.Content.Headers.ContentDisposition?.FileName,
                StringComparison.Ordinal);
            using var stale = await client.GetAsync($"/campaigns/videos/{video.DeliverableId}?v=9");
            Assert.Equal(System.Net.HttpStatusCode.NotFound, stale.StatusCode);
            using var caption = await client.GetAsync($"/campaigns/videos/{video.DeliverableId}/caption");
            Assert.Equal(System.Net.HttpStatusCode.OK, caption.StatusCode);
            using var zip = await client.GetAsync($"/campaigns/{queued.Id}/library.zip");
            Assert.Equal(System.Net.HttpStatusCode.OK, zip.StatusCode);
            Assert.Equal("application/zip", zip.Content.Headers.ContentType?.MediaType);

            var stranger = await CreateOwnerAndPropertyAsync("library-routes-stranger");
            using var other = web.CreateClient();
            other.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(HeaderAuthenticationHandler.SchemeName, stranger.UserId);
            using var denied = await other.GetAsync($"/campaigns/videos/{video.DeliverableId}");
            Assert.Equal(System.Net.HttpStatusCode.NotFound, denied.StatusCode);
            using var deniedZip = await other.GetAsync($"/campaigns/{queued.Id}/library.zip");
            Assert.Equal(System.Net.HttpStatusCode.NotFound, deniedZip.StatusCode);
        }
        finally
        {
            await SetLibrarySwitchAsync(admin, false, false);
        }
    }
}
