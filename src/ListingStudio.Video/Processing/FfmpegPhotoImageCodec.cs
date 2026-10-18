using System.Globalization;
using System.Text;
using ListingStudio.Application.Properties;
using ListingStudio.Video.Configuration;
using Microsoft.Extensions.Options;

namespace ListingStudio.Video.Processing;

/// <summary>
/// Decodes photos to RGB pixels and encodes them again with FFmpeg, which the Web and Worker images already carry.
/// FFmpeg applies a JPEG's EXIF orientation while decoding, so the pixels are always upright.
/// </summary>
public sealed class FfmpegPhotoImageCodec(IOptions<FfmpegOptions> options) : IPhotoImageCodec
{
    private const int TimeoutSeconds = 120;

    public async Task<PhotoPixels> DecodeAsync(
        byte[] image,
        string mimeType,
        int boxWidth,
        int boxHeight,
        PhotoScaling scaling,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(boxWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(boxHeight);
        return await InTemporaryDirectoryAsync(async directory =>
        {
            var inputPath = Path.Combine(directory, $"input{Extension(mimeType)}");
            var outputPath = Path.Combine(directory, "output.ppm");
            await File.WriteAllBytesAsync(inputPath, image, cancellationToken);
            await RunAsync(directory, BuildDecodeArguments(inputPath, outputPath, boxWidth, boxHeight, scaling), cancellationToken);
            return ReadPpm(await File.ReadAllBytesAsync(outputPath, cancellationToken));
        });
    }

    public Task<byte[]> EncodeJpegAsync(PhotoPixels photo, CancellationToken cancellationToken = default) =>
        EncodeAsync(photo.Width, photo.Height, "rgb24", photo.Rgb, "output.jpg", ["-q:v", "2"], cancellationToken);

    public Task<byte[]> EncodePngAsync(PhotoPixels photo, CancellationToken cancellationToken = default) =>
        EncodeAsync(photo.Width, photo.Height, "rgb24", photo.Rgb, "output.png", ["-pix_fmt", "rgb24"], cancellationToken);

    public Task<byte[]> EncodeMaskPngAsync(
        int width,
        int height,
        PhotoRegion editable,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var insideRow = y >= editable.Y && y < editable.Y + editable.Height;
            for (var x = 0; x < width; x++)
            {
                var inside = insideRow && x >= editable.X && x < editable.X + editable.Width;
                rgba[(y * width + x) * 4 + 3] = inside ? (byte)0 : (byte)255;
            }
        }

        return EncodeAsync(width, height, "rgba", rgba, "mask.png", ["-pix_fmt", "rgba"], cancellationToken);
    }

    public static IReadOnlyList<string> BuildDecodeArguments(
        string inputPath,
        string outputPath,
        int boxWidth,
        int boxHeight,
        PhotoScaling scaling)
    {
        var scale = scaling switch
        {
            PhotoScaling.ShrinkToFit => $"scale='min(iw,{boxWidth})':'min(ih,{boxHeight})':force_original_aspect_ratio=decrease:flags=lanczos",
            PhotoScaling.Fit => $"scale={boxWidth}:{boxHeight}:force_original_aspect_ratio=decrease:flags=lanczos",
            PhotoScaling.Exact => $"scale={boxWidth}:{boxHeight}:flags=lanczos",
            _ => throw new ArgumentOutOfRangeException(nameof(scaling)),
        };
        return
        [
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-i", inputPath,
            "-frames:v", "1",
            "-vf", scale,
            "-pix_fmt", "rgb24",
            "-c:v", "ppm",
            "-f", "image2",
            outputPath,
        ];
    }

    /// <summary>Reads a binary PPM (P6) image, the raw RGB format FFmpeg writes with its dimensions.</summary>
    public static PhotoPixels ReadPpm(ReadOnlySpan<byte> ppm)
    {
        var offset = 0;
        var magic = NextToken(ppm, ref offset);
        var width = int.Parse(NextToken(ppm, ref offset), CultureInfo.InvariantCulture);
        var height = int.Parse(NextToken(ppm, ref offset), CultureInfo.InvariantCulture);
        var maximum = int.Parse(NextToken(ppm, ref offset), CultureInfo.InvariantCulture);
        if (magic != "P6" || maximum != 255 || width <= 0 || height <= 0)
        {
            throw new InvalidDataException("The decoded photo is not an 8-bit RGB image.");
        }

        // Exactly one whitespace byte separates the header from the pixels.
        offset++;
        var length = checked(width * height * 3);
        if (ppm.Length - offset < length)
        {
            throw new InvalidDataException("The decoded photo is incomplete.");
        }

        return new PhotoPixels(width, height, ppm.Slice(offset, length).ToArray());
    }

    private async Task<byte[]> EncodeAsync(
        int width,
        int height,
        string pixelFormat,
        byte[] pixels,
        string outputName,
        IReadOnlyList<string> outputArguments,
        CancellationToken cancellationToken)
    {
        return await InTemporaryDirectoryAsync(async directory =>
        {
            var inputPath = Path.Combine(directory, "input.raw");
            var outputPath = Path.Combine(directory, outputName);
            await File.WriteAllBytesAsync(inputPath, pixels, cancellationToken);
            await RunAsync(
                directory,
                [
                    "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                    "-f", "rawvideo", "-pix_fmt", pixelFormat,
                    "-s", $"{width.ToString(CultureInfo.InvariantCulture)}x{height.ToString(CultureInfo.InvariantCulture)}",
                    "-i", inputPath,
                    "-frames:v", "1",
                    .. outputArguments,
                    outputPath,
                ],
                cancellationToken);
            return await File.ReadAllBytesAsync(outputPath, cancellationToken);
        });
    }

    private Task<FfmpegProcessOutput> RunAsync(string directory, IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        FfmpegProcess.RunAsync(options.Value.ExecutablePath, directory, arguments, TimeoutSeconds, cancellationToken);

    private static async Task<T> InTemporaryDirectoryAsync<T>(Func<string, Task<T>> work)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"listing-studio-photo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            return await work(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string NextToken(ReadOnlySpan<byte> data, ref int offset)
    {
        while (offset < data.Length && (char.IsWhiteSpace((char)data[offset]) || data[offset] == '#'))
        {
            if (data[offset] == '#')
            {
                while (offset < data.Length && data[offset] != '\n')
                {
                    offset++;
                }
            }
            else
            {
                offset++;
            }
        }

        var start = offset;
        while (offset < data.Length && !char.IsWhiteSpace((char)data[offset]))
        {
            offset++;
        }

        if (start == offset || offset - start > 16)
        {
            throw new InvalidDataException("The decoded photo has an unreadable header.");
        }

        return Encoding.ASCII.GetString(data[start..offset]);
    }

    private static string Extension(string mimeType) => mimeType.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/webp" => ".webp",
        _ => ".jpg",
    };
}
