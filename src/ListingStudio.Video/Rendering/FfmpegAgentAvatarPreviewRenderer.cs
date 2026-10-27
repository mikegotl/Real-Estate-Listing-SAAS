using ListingStudio.Application.Campaigns;
using ListingStudio.Video.Configuration;
using ListingStudio.Video.Processing;
using Microsoft.Extensions.Options;

namespace ListingStudio.Video.Rendering;

/// <summary>
/// Creates a short, silent preview of the same approved-headshot presenter treatment used by campaign videos.
/// It intentionally does not synthesize speech, lip movement, or a new likeness.
/// </summary>
public sealed class FfmpegAgentAvatarPreviewRenderer(IOptions<FfmpegOptions> options)
    : IAgentAvatarPreviewRenderer
{
    private const int TimeoutSeconds = 120;

    public async Task<byte[]> RenderAsync(
        byte[] portrait,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(portrait);
        if (portrait.Length == 0)
        {
            throw new InvalidDataException("The agent headshot is empty.");
        }

        var directory = Path.Combine(Path.GetTempPath(), $"listing-studio-avatar-preview-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var inputPath = Path.Combine(directory, $"portrait{Extension(contentType)}");
            var outputPath = Path.Combine(directory, "avatar-preview.mp4");
            await File.WriteAllBytesAsync(inputPath, portrait, cancellationToken);
            await FfmpegProcess.RunAsync(
                options.Value.ExecutablePath,
                directory,
                [
                    "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                    "-f", "lavfi", "-i", "color=c=0x0D1B2A:s=720x1280:r=30:d=6",
                    "-loop", "1", "-i", inputPath,
                    "-filter_complex",
                    "[1:v]scale=440:580:force_original_aspect_ratio=decrease:flags=lanczos," +
                    "pad=440:580:(ow-iw)/2:(oh-ih)/2:color=0xEEE8DE,format=rgba," +
                    "fade=t=in:st=0:d=0.6:alpha=1,fade=t=out:st=5.2:d=0.8:alpha=1[p];" +
                    "[0:v][p]overlay=(W-w)/2:(H-h)/2:shortest=1," +
                    "drawbox=x=0:y=1268:w=iw:h=12:color=0xCD9E4C:t=fill,format=yuv420p[out]",
                    "-map", "[out]", "-t", "6", "-r", "30",
                    "-c:v", "libx264", "-preset", "veryfast", "-crf", "23",
                    "-pix_fmt", "yuv420p", "-movflags", "+faststart", "-an",
                    outputPath,
                ],
                TimeoutSeconds,
                cancellationToken);
            var result = await File.ReadAllBytesAsync(outputPath, cancellationToken);
            return result.Length > 0
                ? result
                : throw new InvalidDataException("The agent-presenter preview renderer returned an empty video.");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static string Extension(string contentType) => contentType.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/webp" => ".webp",
        _ => ".jpg",
    };
}
