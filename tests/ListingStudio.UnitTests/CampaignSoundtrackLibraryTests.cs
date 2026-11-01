using ListingStudio.Application.Videos;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Video.Rendering;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class CampaignSoundtrackLibraryTests
{
    public static TheoryData<CampaignSoundtrack> LibraryTracks()
    {
        var data = new TheoryData<CampaignSoundtrack>();
        foreach (var track in CampaignCreativeDirector.LibrarySoundtracks)
        {
            data.Add(track);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(LibraryTracks))]
    public async Task EveryLibraryTrackIsBundledWithTheRendererAndCopiedForARender(CampaignSoundtrack track)
    {
        var assetId = CampaignCreativeDirector.SoundtrackAssetId(track);
        var directory = Path.Combine(Path.GetTempPath(), "soundtrack-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var asset = await new OriginalCampaignSoundtrackService().MaterializeAsync(assetId, 30_000, directory);

            Assert.NotNull(asset);
            Assert.Equal(assetId, asset.AssetId);
            Assert.True(new FileInfo(asset.FilePath).Length > 500_000, "The bundled recording should be a full track.");
            Assert.NotEqual(120, CampaignCreativeDirector.BeatsPerMinute(assetId));
            Assert.Equal(track, CampaignVideoEditor.TrackOf(new Domain.Videos.MusicPlan(
                assetId, Domain.Videos.MusicMood.None, 0, 30_000, -18, 500, 500, -12)));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task UnknownMusicIsRejectedAndNoMusicNeedsNoFile()
    {
        var service = new OriginalCampaignSoundtrackService();

        Assert.Null(await service.MaterializeAsync(null, 30_000, Path.GetTempPath()));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.MaterializeAsync("someone-elses-song", 30_000, Path.GetTempPath()));
    }

    [Fact]
    public void LibraryHasDistinctTracksAndNames()
    {
        var tracks = CampaignCreativeDirector.LibrarySoundtracks;

        Assert.Contains(CampaignSoundtrack.LuxuryCinematic, tracks);
        Assert.Equal(tracks.Count, tracks.Select(CampaignCreativeDirector.SoundtrackAssetId).Distinct().Count());
        Assert.Equal(tracks.Count, tracks.Select(CampaignVideoEditor.TrackName).Distinct().Count());
        Assert.False(CampaignCreativeDirector.IsLibrarySoundtrack(CampaignSoundtrack.AddInApp));
        Assert.False(CampaignCreativeDirector.IsLibrarySoundtrack(CampaignSoundtrack.AutoByHomeEra));
    }
}
