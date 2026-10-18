using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ListingStudio.AI.Configuration;
using ListingStudio.AI.Properties;
using ListingStudio.Application.Properties;
using ListingStudio.Domain.Properties;
using ListingStudio.Video.Processing;
using Microsoft.Extensions.Options;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class PhotoToneNormalizerTests
{
    [Fact]
    public void LiftsADarkPhotoTowardAnEvenMidTone()
    {
        var photo = Gradient(120, 80, red: 0.35, green: 0.35, blue: 0.35);
        var before = MedianLuma(photo);

        var curve = PhotoToneNormalizer.Normalize(photo);

        Assert.True(curve.Gamma < 1, $"Expected a brightening curve, got gamma {curve.Gamma}.");
        Assert.InRange(MedianLuma(photo), before + 30, 160);
    }

    [Fact]
    public void RemovesMostOfAWarmColorCast()
    {
        var photo = Gradient(120, 80, red: 0.9, green: 0.8, blue: 0.67);
        var (redBefore, _, blueBefore) = Means(photo);

        PhotoToneNormalizer.Normalize(photo);

        var (red, green, blue) = Means(photo);
        var castBefore = redBefore / blueBefore - 1;
        var castAfter = red / blue - 1;
        Assert.True(castAfter < castBefore / 2, $"Red to blue was {castBefore:P0} warmer, now {castAfter:P0}.");
        Assert.InRange(red / green, 0.95, 1.08);
    }

    [Fact]
    public void LeavesAWellExposedNeutralPhotoAlmostUntouched()
    {
        var photo = Gradient(160, 100, red: 1, green: 1, blue: 1);
        var before = photo.Rgb.ToArray();

        var curve = PhotoToneNormalizer.Normalize(photo);

        Assert.Equal(1, curve.Gamma);
        var largestChange = before.Zip(photo.Rgb, (left, right) => Math.Abs(left - right)).Max();
        Assert.InRange(largestChange, 0, 6);
    }

    [Fact]
    public void NeverDarkensABrightAiryPhotoMuch()
    {
        var photo = Gradient(120, 80, red: 1, green: 1, blue: 1, offset: 90);
        var before = MedianLuma(photo);

        PhotoToneNormalizer.Normalize(photo);

        Assert.InRange(MedianLuma(photo), before - 20, 255);
    }

    // A left-to-right ramp scaled per channel, like a softly lit wall with a tint.
    private static PhotoPixels Gradient(int width, int height, double red, double green, double blue, int offset = 0)
    {
        var pixels = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var level = Math.Min(255, offset + x * (255 - offset) / (width - 1));
                var index = (y * width + x) * 3;
                pixels[index] = (byte)Math.Round(level * red);
                pixels[index + 1] = (byte)Math.Round(level * green);
                pixels[index + 2] = (byte)Math.Round(level * blue);
            }
        }

        return new PhotoPixels(width, height, pixels);
    }

    private static double MedianLuma(PhotoPixels photo)
    {
        var lumas = Enumerable.Range(0, photo.Width * photo.Height)
            .Select(pixel => 0.2126 * photo.Rgb[pixel * 3] + 0.7152 * photo.Rgb[pixel * 3 + 1] + 0.0722 * photo.Rgb[pixel * 3 + 2])
            .Order()
            .ToArray();
        return lumas[lumas.Length / 2];
    }

    private static (double Red, double Green, double Blue) Means(PhotoPixels photo)
    {
        var count = photo.Width * photo.Height;
        double red = 0, green = 0, blue = 0;
        for (var pixel = 0; pixel < count; pixel++)
        {
            red += photo.Rgb[pixel * 3];
            green += photo.Rgb[pixel * 3 + 1];
            blue += photo.Rgb[pixel * 3 + 2];
        }

        return (red / count, green / count, blue / count);
    }
}

public sealed class PhotoEditCanvasTests
{
    [Theory]
    [InlineData(4_032, 3_024, 1_536, 1_024)]
    [InlineData(6_000, 4_000, 1_536, 1_024)]
    [InlineData(3_024, 4_032, 1_024, 1_536)]
    [InlineData(2_000, 2_000, 1_024, 1_024)]
    [InlineData(2_200, 2_000, 1_024, 1_024)]
    public void PicksTheCanvasClosestToThePhotosShape(int width, int height, int canvasWidth, int canvasHeight)
    {
        Assert.Equal((canvasWidth, canvasHeight), PhotoEditCanvas.SizeFor(width, height));
    }

    [Fact]
    public void PadsAPhotoInTheCenterAndCropsTheSamePixelsBackOut()
    {
        var pixels = Enumerable.Range(0, 4 * 3 * 3).Select(value => (byte)(value + 1)).ToArray();
        var photo = new PhotoPixels(4, 3, pixels);

        var canvas = PhotoEditCanvas.Pad(photo, 8, 5, out var region);

        Assert.Equal(new PhotoRegion(2, 1, 4, 3), region);
        Assert.Equal(PhotoEditCanvas.PaddingGray, canvas.Rgb[0]);
        Assert.Equal(pixels, PhotoEditCanvas.Crop(canvas, region).Rgb);
    }

    [Fact]
    public void RejectsAPhotoLargerThanTheCanvas()
    {
        var photo = new PhotoPixels(10, 10, new byte[300]);

        Assert.Throws<ArgumentException>(() => PhotoEditCanvas.Pad(photo, 8, 8, out _));
    }
}

public sealed class PropertyMediaPhotoFixTests
{
    [Fact]
    public void AFixKeepsTheUploadAndALaterFixReplacesOnlyTheFixedVersion()
    {
        var media = CreateAnalyzedMedia();
        var now = DateTimeOffset.UtcNow;

        Assert.Null(media.ApplyFix(PhotoFix.BrightnessAndColor, "fixed-1.jpg", "image/jpeg", 900, 1_600, 1_200, now));

        Assert.True(media.IsEdited);
        Assert.Equal(("upload.png", "image/png", 1_024L, 1_920, 1_080), (media.OriginalBlobPath, media.OriginalMimeType, media.OriginalFileSize!.Value, media.OriginalWidth!.Value, media.OriginalHeight!.Value));
        Assert.Equal(("fixed-1.jpg", "image/jpeg", 1_600, 1_200), (media.BlobPath, media.MimeType, media.Width, media.Height));
        Assert.Equal(PropertyMediaAnalysisStatus.Pending, media.AnalysisStatus);
        Assert.Null(media.GetAnalysis());

        Assert.Equal("fixed-1.jpg", media.ApplyFix(PhotoFix.RemoveClutter, "fixed-2.jpg", "image/jpeg", 800, 1_365, 1_024, now));
        Assert.Equal(PhotoFix.BrightnessAndColor | PhotoFix.RemoveClutter, media.AppliedFixes);
        Assert.Equal("upload.png", media.OriginalBlobPath);
    }

    [Fact]
    public void RevertPutsTheUploadBackAndReturnsTheFixedFile()
    {
        var media = CreateAnalyzedMedia();
        media.ApplyFix(PhotoFix.RemoveClutter, "fixed.jpg", "image/jpeg", 800, 1_365, 1_024, DateTimeOffset.UtcNow);

        Assert.Equal("fixed.jpg", media.RevertToOriginal(DateTimeOffset.UtcNow));

        Assert.False(media.IsEdited);
        Assert.Equal(("upload.png", "image/png", 1_024L, 1_920, 1_080), (media.BlobPath, media.MimeType, media.FileSize, media.Width, media.Height));
        Assert.Equal(PhotoFix.None, media.AppliedFixes);
        Assert.Throws<InvalidOperationException>(() => media.RevertToOriginal(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void AFixNeedsItsOwnFileAndExactlyOneKind()
    {
        var media = CreateAnalyzedMedia();

        Assert.Throws<ArgumentException>(() =>
            media.ApplyFix(PhotoFix.BrightnessAndColor, "upload.png", "image/jpeg", 1, 1, 1, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            media.ApplyFix(PhotoFix.BrightnessAndColor | PhotoFix.RemoveClutter, "fixed.jpg", "image/jpeg", 1, 1, 1, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void AFixRequestRetriesOnlyWhileAttemptsRemain()
    {
        var now = DateTimeOffset.UtcNow;
        var fix = PropertyPhotoFix.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), PhotoFix.RemoveClutter, "upload.png", now);
        for (var attempt = 1; attempt < PropertyPhotoFix.MaximumAttempts; attempt++)
        {
            fix.Begin(now);
            fix.Fail("connection reset", now.AddMinutes(1));
            Assert.True(fix.IsInProgress);
        }

        fix.Begin(now);
        fix.Fail("connection reset", now.AddMinutes(1));
        Assert.False(fix.IsInProgress);
        Assert.Null(fix.NextAttemptAtUtc);
        Assert.Throws<InvalidOperationException>(() => fix.Begin(now));
    }

    private static PropertyMedia CreateAnalyzedMedia()
    {
        var media = PropertyMedia.Create(Guid.NewGuid(), Guid.NewGuid(), "upload.png", "kitchen.png", "image/png", 1_024, 1_920, 1_080, 0);
        media.BeginAnalysis(DateTimeOffset.UtcNow);
        media.CompleteAnalysis(
            new PropertyMediaAnalysis(PropertyMediaCategory.Kitchen, "Kitchen", 40, 30, false, true, false, ["Clutter on counters"], "A kitchen.", 3),
            DateTimeOffset.UtcNow);
        return media;
    }
}

public sealed class OpenAIPhotoClutterRemoverTests
{
    private static readonly byte[] EditedPng = [137, 80, 78, 71, 1, 2, 3];

    [Fact]
    public async Task SendsThePaddedPhotoAndMaskWithAPromptThatKeepsTheHomeAsItIs()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, $$"""{"data":[{"b64_json":"{{Convert.ToBase64String(EditedPng)}}"}]}""");
        var remover = CreateRemover(handler);

        var result = await remover.RemoveClutterAsync(new PhotoClutterRemovalRequest([1, 2], [3, 4], 1_536, 1_024));

        Assert.Equal(EditedPng, result);
        Assert.Equal(new Uri("https://api.openai.test/v1/images/edits"), handler.RequestUri);
        Assert.Equal(new AuthenticationHeaderValue("Bearer", "test-api-key"), handler.Authorization);
        Assert.Equal("gpt-image-test", handler.Fields["model"]);
        Assert.Equal("1536x1024", handler.Fields["size"]);
        Assert.Equal("high", handler.Fields["input_fidelity"]);
        Assert.Equal("png", handler.Fields["output_format"]);
        Assert.Contains("Do not repair, renovate, restyle or improve", handler.Fields["prompt"], StringComparison.Ordinal);
        Assert.Contains("do not add any object", handler.Fields["prompt"], StringComparison.Ordinal);
        Assert.Equal(["photo.png", "mask.png"], handler.Files);
    }

    [Fact]
    public async Task AModerationBlockIsNotRetriedButAServerErrorIs()
    {
        var blocked = CreateRemover(new RecordingHandler(HttpStatusCode.BadRequest, """{"error":{"code":"moderation_blocked","message":"x"}}"""));
        var exception = await Assert.ThrowsAsync<PhotoFixRejectedException>(() =>
            blocked.RemoveClutterAsync(new PhotoClutterRemovalRequest([1], [2], 1_024, 1_024)));
        Assert.StartsWith("OpenAI declined to edit this photo", exception.Message, StringComparison.Ordinal);

        var unavailable = CreateRemover(new RecordingHandler(HttpStatusCode.BadGateway, "<html>bad gateway</html>"));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            unavailable.RemoveClutterAsync(new PhotoClutterRemovalRequest([1], [2], 1_024, 1_024)));
    }

    [Fact]
    public void StaysOffUntilSwitchedOnWithAnApiKey()
    {
        using var client = new HttpClient(new RecordingHandler(HttpStatusCode.OK, "{}"));
        Assert.False(new OpenAIPhotoClutterRemover(
            client,
            Options.Create(new OpenAIOptions { ApiKey = "key" }),
            Options.Create(new PhotoFixOptions())).IsEnabled);
        Assert.False(new OpenAIPhotoClutterRemover(
            client,
            Options.Create(new OpenAIOptions()),
            Options.Create(new PhotoFixOptions { RemoveClutterEnabled = true })).IsEnabled);
    }

    private static OpenAIPhotoClutterRemover CreateRemover(RecordingHandler handler) => new(
        new HttpClient(handler),
        Options.Create(new OpenAIOptions { ApiKey = "test-api-key" }),
        Options.Create(new PhotoFixOptions
        {
            RemoveClutterEnabled = true,
            ImageModel = "gpt-image-test",
            ImageEditsEndpoint = "https://api.openai.test/v1/images/edits",
        }));

    private sealed class RecordingHandler(HttpStatusCode status, string responseBody) : HttpMessageHandler
    {
        public Dictionary<string, string> Fields { get; } = [];

        public List<string> Files { get; } = [];

        public Uri? RequestUri { get; private set; }

        public AuthenticationHeaderValue? Authorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Authorization = request.Headers.Authorization;
            foreach (var part in (MultipartFormDataContent)request.Content!)
            {
                var disposition = part.Headers.ContentDisposition!;
                if (disposition.FileName is { } file)
                {
                    Files.Add(file.Trim('"'));
                }
                else
                {
                    Fields[disposition.Name!.Trim('"')] = await part.ReadAsStringAsync(cancellationToken);
                }
            }

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}

public sealed class FfmpegPhotoImageCodecTests
{
    [Fact]
    public void ReadsTheRgbPixelsOfAPpmImage()
    {
        byte[] ppm = [.. "P6\n2 1\n255\n"u8, 10, 20, 30, 40, 50, 60];

        var photo = FfmpegPhotoImageCodec.ReadPpm(ppm);

        Assert.Equal((2, 1), (photo.Width, photo.Height));
        Assert.Equal(new byte[] { 10, 20, 30, 40, 50, 60 }, photo.Rgb);
    }

    [Fact]
    public void RejectsAnIncompletePpmImage()
    {
        byte[] ppm = [.. "P6\n2 2\n255\n"u8, 1, 2, 3];

        Assert.Throws<InvalidDataException>(() => FfmpegPhotoImageCodec.ReadPpm(ppm));
    }

    [Fact]
    public void ShrinkToFitNeverEnlargesAPhoto()
    {
        var arguments = FfmpegPhotoImageCodec.BuildDecodeArguments("in.jpg", "out.ppm", 6_000, 6_000, PhotoScaling.ShrinkToFit);

        Assert.Contains("scale='min(iw,6000)':'min(ih,6000)':force_original_aspect_ratio=decrease:flags=lanczos", arguments);
        Assert.Equal("rgb24", arguments[arguments.ToList().IndexOf("-pix_fmt") + 1]);
    }
}
