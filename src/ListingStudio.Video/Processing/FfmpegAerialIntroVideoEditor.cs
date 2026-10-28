using System.Globalization;
using ListingStudio.Application.Videos;
using ListingStudio.Video.Configuration;
using Microsoft.Extensions.Options;

namespace ListingStudio.Video.Processing;

/// <summary>
/// Plays a generated pull-back backwards so the drone descends onto the real Exterior front photo, and burns in a
/// two-line "AI-generated aerial / Not actual footage" label, centered so it survives the vertical crop.
/// </summary>
public sealed class FfmpegAerialIntroVideoEditor(IOptions<FfmpegOptions> options) : IAerialIntroVideoEditor
{
    public const string LabelFirstLine = "AI-generated aerial";
    public const string LabelSecondLine = "Not actual footage";

    /// <summary>Wider clips are scaled down before reversing, which holds every frame in memory.</summary>
    public const int MaximumWidth = 1_920;

    public async Task<AerialIntroEditResult> PrepareAsync(
        Stream generatedClip,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(generatedClip);
        var configuration = options.Value;
        var directory = Path.Combine(Path.GetTempPath(), $"listing-studio-aerial-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var inputPath = Path.Combine(directory, "generated.mp4");
            var outputPath = Path.Combine(directory, "intro.mp4");
            await using (var target = new FileStream(inputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920, true))
            {
                await generatedClip.CopyToAsync(target, cancellationToken);
            }

            await FfmpegProcess.RunAsync(
                configuration.ExecutablePath,
                directory,
                BuildArguments(inputPath, outputPath),
                configuration.RenderTimeoutSeconds,
                cancellationToken);
            var probe = await FfmpegProcess.RunAsync(
                configuration.ProbeExecutablePath,
                directory,
                ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", outputPath],
                60,
                cancellationToken);
            if (!decimal.TryParse(probe.StandardOutput.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                || seconds <= 0)
            {
                throw new InvalidDataException("The drone intro has no readable duration.");
            }

            var content = new MemoryStream(await File.ReadAllBytesAsync(outputPath, cancellationToken));
            return new AerialIntroEditResult(content, content.Length, (int)Math.Round(seconds * 1_000));
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public static IReadOnlyList<string> BuildArguments(string inputPath, string outputPath)
    {
        // Line height and margins follow the frame height, so the label reads the same at 720p and 1080p.
        const string size = "fontsize=h/26";
        const string box = "box=1:boxcolor=black@0.6:boxborderw=10";
        var label =
            $"drawtext=font='Sans':text='{LabelFirstLine}':fontcolor=white:{size}:{box}:x=(w-tw)/2:y=h*0.93-h/26*2.6,"
            + $"drawtext=font='Sans':text='{LabelSecondLine}':fontcolor=white:{size}:{box}:x=(w-tw)/2:y=h*0.93-h/26";
        return
        [
            "-hide_banner",
            "-loglevel", "warning",
            "-nostdin",
            "-y",
            "-i", inputPath,
            "-an",
            "-vf", $"scale='min({MaximumWidth},iw)':-2:flags=lanczos,setsar=1,format=yuv420p,reverse,fps=30,{label}",
            "-c:v", "libx264",
            "-preset", "medium",
            "-crf", "18",
            "-pix_fmt", "yuv420p",
            "-movflags", "+faststart",
            outputPath,
        ];
    }
}
