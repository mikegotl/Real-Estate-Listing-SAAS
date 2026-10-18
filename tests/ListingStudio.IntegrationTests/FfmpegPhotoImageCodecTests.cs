using System.Diagnostics;
using ListingStudio.Application.Properties;
using ListingStudio.Video.Configuration;
using ListingStudio.Video.Processing;
using Microsoft.Extensions.Options;
using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed class FfmpegPhotoImageCodecTests
{
    [Fact]
    public async Task DecodesAPhoneJpegUprightAndEncodesTheFixedPhotoTheSameWayUp()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("RUN_FFMPEG_E2E"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var ffmpeg = Environment.GetEnvironmentVariable("FFMPEG_PATH") ?? "ffmpeg";
        var codec = new FfmpegPhotoImageCodec(Options.Create(new FfmpegOptions { ExecutablePath = ffmpeg }));
        // A landscape sensor image tagged "rotate 90° clockwise", the way phones save portrait photos.
        var portrait = WithExifOrientation(await RenderTestJpegAsync(ffmpeg, 320, 240), orientation: 6);

        var upright = await codec.DecodeAsync(portrait, "image/jpeg", 6_000, 6_000, PhotoScaling.ShrinkToFit);
        Assert.Equal((240, 320), (upright.Width, upright.Height));

        PhotoToneNormalizer.Normalize(upright);
        var jpeg = await codec.EncodeJpegAsync(upright);
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, jpeg[..2]);
        var reread = await codec.DecodeAsync(jpeg, "image/jpeg", 6_000, 6_000, PhotoScaling.ShrinkToFit);
        Assert.Equal((240, 320), (reread.Width, reread.Height));

        var (canvasWidth, canvasHeight) = PhotoEditCanvas.SizeFor(upright.Width, upright.Height);
        var fitted = await codec.DecodeAsync(portrait, "image/jpeg", canvasWidth, canvasHeight, PhotoScaling.Fit);
        Assert.Equal((1_024, 1_365), (fitted.Width, fitted.Height));
        var canvas = PhotoEditCanvas.Pad(fitted, canvasWidth, canvasHeight, out var region);
        var png = await codec.EncodePngAsync(canvas);
        var mask = await codec.EncodeMaskPngAsync(canvasWidth, canvasHeight, region);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, mask[..4]);
        var edited = await codec.DecodeAsync(png, "image/png", canvasWidth, canvasHeight, PhotoScaling.Exact);
        var cropped = PhotoEditCanvas.Crop(edited, region);
        Assert.Equal((1_024, 1_365), (cropped.Width, cropped.Height));
    }

    private static async Task<byte[]> RenderTestJpegAsync(string ffmpeg, int width, int height)
    {
        var path = Path.Combine(Path.GetTempPath(), $"listing-studio-photo-test-{Guid.NewGuid():N}.jpg");
        try
        {
            using var process = Process.Start(new ProcessStartInfo(ffmpeg)
            {
                ArgumentList =
                {
                    "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                    "-f", "lavfi", "-i", $"testsrc2=s={width}x{height}",
                    "-frames:v", "1", path,
                },
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, error);
            return await File.ReadAllBytesAsync(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // Inserts a minimal big-endian EXIF segment holding only the orientation tag right after the JPEG's SOI marker.
    private static byte[] WithExifOrientation(byte[] jpeg, byte orientation)
    {
        byte[] exif =
        [
            0xFF, 0xE1, 0x00, 0x22,
            (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0x00, 0x00,
            (byte)'M', (byte)'M', 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08,
            0x00, 0x01,
            0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, orientation, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
        ];
        return [.. jpeg[..2], .. exif, .. jpeg[2..]];
    }
}
