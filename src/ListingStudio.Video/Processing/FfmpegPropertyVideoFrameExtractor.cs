using System.Globalization;
using ListingStudio.Application.Properties;
using ListingStudio.Video.Configuration;
using Microsoft.Extensions.Options;

namespace ListingStudio.Video.Processing;

/// <summary>
/// Samples the video twice a second at low resolution, picks the sharpest sample of each view, and exports those
/// moments as full-resolution JPEG stills (at most 1920 pixels wide). HDR phone video is converted to standard range
/// so the stills are not grey and flat.
/// </summary>
public sealed class FfmpegPropertyVideoFrameExtractor(IOptions<FfmpegOptions> options) : IPropertyVideoFrameExtractor
{
    public const int MaximumStillWidth = 1_920;

    public async Task<IReadOnlyList<VideoFrameCandidate>> ExtractCandidatesAsync(
        Stream content,
        string originalFilename,
        int maximumCandidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(originalFilename);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCandidates);
        var directory = Path.Combine(Path.GetTempPath(), $"listing-studio-frames-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var inputPath = Path.Combine(directory, $"input{SafeExtension(originalFilename)}");
            await using (var target = new FileStream(inputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920, true))
            {
                if (content.CanSeek)
                {
                    content.Position = 0;
                }

                await content.CopyToAsync(target, cancellationToken);
            }

            var isHdr = await IsHdrAsync(options.Value, inputPath, directory, cancellationToken);
            var samplesPath = Path.Combine(directory, "samples.gray");
            await FfmpegProcess.RunAsync(
                options.Value.ExecutablePath,
                directory,
                BuildSamplingArguments(inputPath, samplesPath, isHdr),
                options.Value.EnhancementTimeoutSeconds,
                cancellationToken);
            var samples = MeasureSamples(await File.ReadAllBytesAsync(samplesPath, cancellationToken));
            var chosen = VideoFrameSampling.ChooseFrames(samples, maximumCandidates);

            var candidates = new List<VideoFrameCandidate>(chosen.Count);
            foreach (var index in chosen)
            {
                var timestampMs = index * 1_000 / VideoFrameSampling.SamplesPerSecond;
                var stillPath = Path.Combine(directory, $"still-{index}.jpg");
                await FfmpegProcess.RunAsync(
                    options.Value.ExecutablePath,
                    directory,
                    BuildStillArguments(inputPath, stillPath, timestampMs, isHdr),
                    120,
                    cancellationToken);
                if (File.Exists(stillPath) && new FileInfo(stillPath).Length > 0)
                {
                    candidates.Add(new VideoFrameCandidate(
                        timestampMs,
                        samples[index].Sharpness,
                        await File.ReadAllBytesAsync(stillPath, cancellationToken)));
                }
            }

            return candidates;
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Whether the first video stream is HDR; a probe failure is read as ordinary video.</summary>
    internal static async Task<bool> IsHdrAsync(
        FfmpegOptions options,
        string inputPath,
        string directory,
        CancellationToken cancellationToken)
    {
        try
        {
            var probe = await FfmpegProcess.RunAsync(
                options.ProbeExecutablePath,
                directory,
                HdrToneMapping.BuildTransferProbeArguments(inputPath),
                30,
                cancellationToken);
            return HdrToneMapping.IsHdrTransfer(probe.StandardOutput.Trim());
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public static IReadOnlyList<string> BuildSamplingArguments(string inputPath, string samplesPath, bool isHdr = false) =>
    [
        "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
        "-i", inputPath,
        "-map", "0:v:0", "-an", "-sn",
        // Sampling and shrinking come before the HDR conversion, which is slow on full-size frames.
        "-vf", $"fps={VideoFrameSampling.SamplesPerSecond},scale={VideoFrameSampling.SampleWidth}:{VideoFrameSampling.SampleHeight},"
            + $"{(isHdr ? HdrToneMapping.ToStandardRange + "," : string.Empty)}format=gray",
        "-f", "rawvideo",
        samplesPath,
    ];

    public static IReadOnlyList<string> BuildStillArguments(string inputPath, string stillPath, int timestampMs, bool isHdr = false) =>
    [
        "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
        "-ss", (timestampMs / 1_000d).ToString("0.###", CultureInfo.InvariantCulture),
        "-i", inputPath,
        "-map", "0:v:0", "-frames:v", "1",
        "-vf", $"{(isHdr ? HdrToneMapping.ToStandardRange + "," : string.Empty)}scale='min({MaximumStillWidth},iw)':-2",
        "-q:v", "2",
        stillPath,
    ];

    private static FrameSample[] MeasureSamples(byte[] raw)
    {
        const int frameSize = VideoFrameSampling.SampleWidth * VideoFrameSampling.SampleHeight;
        var samples = new FrameSample[raw.Length / frameSize];
        for (var index = 0; index < samples.Length; index++)
        {
            var frame = raw.AsSpan(index * frameSize, frameSize);
            var previous = index == 0 ? ReadOnlySpan<byte>.Empty : raw.AsSpan((index - 1) * frameSize, frameSize);
            samples[index] = VideoFrameSampling.Measure(
                frame,
                previous,
                VideoFrameSampling.SampleWidth,
                VideoFrameSampling.SampleHeight);
        }

        return samples;
    }

    private static string SafeExtension(string filename)
    {
        var extension = Path.GetExtension(filename).ToLowerInvariant();
        return extension is ".mp4" or ".m4v" or ".mov" or ".webm" ? extension : ".video";
    }
}
