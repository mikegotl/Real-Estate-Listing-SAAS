using System.Text.Json;
using ListingStudio.Application.Audio;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Videos;
using ListingStudio.Video.Configuration;
using ListingStudio.Video.Rendering;
using Microsoft.Extensions.Options;
using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed partial class FfmpegRenderingTests
{
    // Real editor and renderer; synthetic pictures and tones. This is not a browser or AI-quality test.
    [Theory]
    [InlineData("short-voiceover", CampaignVoiceoverMode.ShortHook, CampaignPace.Fast, 30)]
    [InlineData("music-text-only", CampaignVoiceoverMode.None, CampaignPace.Fast, 30)]
    [InlineData("full-voiceover", CampaignVoiceoverMode.Full, CampaignPace.Cinematic, 60)]
    public async Task TikTokReviewOutputsArePlayable(
        string caseId, CampaignVoiceoverMode voiceover, CampaignPace pace, int seconds)
    {
        if (Environment.GetEnvironmentVariable("RUN_FFMPEG_E2E") != "1")
        {
            return;
        }

        var root = Environment.GetEnvironmentVariable("TIKTOK_REVIEW_DIR")
            ?? Path.Combine(Path.GetTempPath(), $"listingstudio-tiktok-review-{Guid.NewGuid():N}");
        var directory = Path.Combine(Path.GetFullPath(root), caseId);
        Directory.CreateDirectory(directory);
        var reportPath = Path.Combine(directory, "result.json");
        var report = new Dictionary<string, object?>
        {
            ["case_id"] = caseId,
            ["scope"] = "synthetic editor/render regression; no browser or paid providers",
            ["voiceover"] = voiceover.ToString(),
            ["pace"] = pace.ToString(),
            ["expected_seconds"] = seconds,
            ["technical_status"] = "failed",
            ["editorial_status"] = "needs human review; narration uses test tones, not speech",
        };
        try
        {
            var mediaId = Guid.NewGuid();
            var picture = Path.Combine(directory, "fixture.ppm");
            await WriteSampleImageAsync(picture);
            var master = CreateMasterSpecification(mediaId) with
            {
                FactBindings =
                [
                    new("property.listingPrice", "$450,000", FactSource.VerifiedProperty, "ListingPrice"),
                    new("property.bedBath", "3 beds • 2 baths", FactSource.VerifiedProperty, "Bedrooms+Bathrooms"),
                    new("story.openingHook", "Explore this demonstration listing.", FactSource.PropertyStory, "OpeningHook"),
                    new("story.voiceover", "Explore this demonstration listing.", FactSource.PropertyStory, "VoiceoverScript"),
                    new("story.closingCta", "Contact the listing team.", FactSource.PropertyStory, "ClosingCta"),
                ],
                Brand = new(null, null, "Demo Agent", "555-0100", null, null, "#17324D", "#F4F0E8"),
                Audio = new AudioPlan(
                    [new NarrationSegment("demo-full", 500, 2_000, "Explore this demonstration listing.", "story.voiceover")],
                    new MusicPlan("demo-music", MusicMood.WarmCinematic, 0, 60_000, -24, 500, 500, -18)),
            };
            var creative = CampaignCreativeOptions.SocialDefault with { Voiceover = voiceover, Pace = pace };
            var specification = ShortFormEditor.Create(new ShortFormEditRequest(
                master, [new ShortFormPhoto(mediaId, 320, 180)], [],
                ShortFormCopyBuilder.DurationFor(voiceover), 500, pace,
                ShortFormCopyBuilder.Build(master, creative, "Demo City", "FL")));
            Assert.Equal(VideoAspectRatio.Vertical9By16, specification.AspectRatio);
            Assert.Equal(seconds * 1_000, specification.Scenes.Sum(scene => scene.DurationMs));
            if (voiceover == CampaignVoiceoverMode.None)
            {
                Assert.Empty(specification.Audio.NarrationSegments);
            }
            else
            {
                Assert.NotEmpty(specification.Audio.NarrationSegments);
            }

            var narrationPath = Path.Combine(directory, "test-tone.wav");
            var musicPath = Path.Combine(directory, "test-music.wav");
            await WriteSampleAudioAsync(narrationPath, 440, seconds, 0.15);
            await WriteSampleAudioAsync(musicPath, 220, seconds, 0.03);
            var narration = voiceover == CampaignVoiceoverMode.None ? null : new VideoRenderNarrationAsset(
                narrationPath,
                new VoiceTimingMetadata([], specification.Audio.NarrationSegments.Select(segment =>
                    new VoiceSegmentTiming(segment.Id, segment.StartMs, segment.StartMs + segment.DurationMs)).ToArray()));
            var options = Options.Create(new FfmpegOptions
            {
                ExecutablePath = Environment.GetEnvironmentVariable("FFMPEG_PATH") ?? "ffmpeg",
                ProbeExecutablePath = Environment.GetEnvironmentVariable("FFPROBE_PATH") ?? "ffprobe",
                RenderTimeoutSeconds = 600,
            });
            var renderer = new FfmpegVideoRenderer(options, Options.Create(new VideoBrandingTemplateOptions
            {
                FontFilePath = File.Exists("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf")
                    ? "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf" : string.Empty,
            }));
            var output = Path.Combine(directory, "video.mp4");
            var rendered = await renderer.RenderAsync(new VideoRenderRequest(
                specification, [new VideoRenderMediaAsset(mediaId, picture, 320, 180)],
                narration, [], new VideoRenderMusicAsset("demo-music", musicPath), output));
            Assert.Equal(0, rendered.ExitCode);
            using var probe = await ProbeAsync(options.Value.ProbeExecutablePath, output);
            var streams = probe.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            var video = Assert.Single(streams.Where(s => s.GetProperty("codec_type").GetString() == "video"));
            var audio = Assert.Single(streams.Where(s => s.GetProperty("codec_type").GetString() == "audio"));
            Assert.Equal(1080, video.GetProperty("width").GetInt32());
            Assert.Equal(1920, video.GetProperty("height").GetInt32());
            Assert.Equal("h264", video.GetProperty("codec_name").GetString());
            Assert.Equal("aac", audio.GetProperty("codec_name").GetString());
            Assert.Equal("30/1", video.GetProperty("r_frame_rate").GetString());
            var duration = double.Parse(probe.RootElement.GetProperty("format").GetProperty("duration").GetString()!,
                System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(duration, seconds - 0.15, seconds + 0.15);
            await RunFfmpegAsync(options.Value.ExecutablePath, "-v", "error", "-xerror", "-i", output, "-f", "null", "-");
            await RunFfmpegAsync(options.Value.ExecutablePath, "-v", "error", "-i", output,
                "-vf", $"fps=1/{seconds / 6},scale=180:320,tile=6x1", "-frames:v", "1",
                Path.Combine(directory, "contact-sheet.jpg"));
            await File.WriteAllTextAsync(Path.Combine(directory, "specification.json"),
                JsonSerializer.Serialize(specification, VideoSpecificationJson.Options));
            report["technical_status"] = "passed";
            report["video"] = "video.mp4";
            report["contact_sheet"] = "contact-sheet.jpg";
            report["checks"] = new[] { "1080x1920", "H.264/AAC", "30fps", "duration", "full-file decode", "voiceover plan" };
        }
        catch (Exception exception)
        {
            report["error"] = exception.Message;
            throw;
        }
        finally
        {
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
