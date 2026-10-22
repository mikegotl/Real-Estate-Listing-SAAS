using ListingStudio.Application.Properties;
using ListingStudio.Video.Configuration;
using Microsoft.Extensions.Options;

namespace ListingStudio.Video.Processing;

/// <summary>
/// Samples the vertical middle of the video twice a second (what a 9:16 crop keeps) and returns the steady, detailed
/// stretches as short-form shot candidates.
/// </summary>
public sealed class FfmpegPropertyVideoClipFinder(IOptions<FfmpegOptions> options) : IPropertyVideoClipFinder
{
    public async Task<IReadOnlyList<VideoClipSpan>> FindClipsAsync(
        Stream content,
        string originalFilename,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(originalFilename);
        var directory = Path.Combine(Path.GetTempPath(), $"listing-studio-clips-{Guid.NewGuid():N}");
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

            var isHdr = await FfmpegPropertyVideoFrameExtractor.IsHdrAsync(options.Value, inputPath, directory, cancellationToken);
            var samplesPath = Path.Combine(directory, "samples.gray");
            await FfmpegProcess.RunAsync(
                options.Value.ExecutablePath,
                directory,
                BuildSamplingArguments(inputPath, samplesPath, isHdr),
                options.Value.EnhancementTimeoutSeconds,
                cancellationToken);
            return VideoClipSampling.FindSpans(MeasureSamples(await File.ReadAllBytesAsync(samplesPath, cancellationToken)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    public static IReadOnlyList<string> BuildSamplingArguments(string inputPath, string samplesPath, bool isHdr = false) =>
    [
        "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
        "-i", inputPath,
        "-map", "0:v:0", "-an", "-sn",
        // Sampling and shrinking come before the HDR conversion, which is slow on full-size frames.
        "-vf", $"fps={VideoClipSampling.SamplesPerSecond},"
            + "crop='min(iw,ih*9/16)':'min(ih,iw*16/9)',"
            + $"scale={VideoClipSampling.SampleWidth}:{VideoClipSampling.SampleHeight},"
            + $"{(isHdr ? HdrToneMapping.ToStandardRange + "," : string.Empty)}format=gray",
        "-f", "rawvideo",
        samplesPath,
    ];

    private static FrameSample[] MeasureSamples(byte[] raw)
    {
        const int frameSize = VideoClipSampling.SampleWidth * VideoClipSampling.SampleHeight;
        var samples = new FrameSample[raw.Length / frameSize];
        for (var index = 0; index < samples.Length; index++)
        {
            var frame = raw.AsSpan(index * frameSize, frameSize);
            var previous = index == 0 ? ReadOnlySpan<byte>.Empty : raw.AsSpan((index - 1) * frameSize, frameSize);
            samples[index] = VideoFrameSampling.Measure(
                frame,
                previous,
                VideoClipSampling.SampleWidth,
                VideoClipSampling.SampleHeight);
        }

        return samples;
    }

    private static string SafeExtension(string filename)
    {
        var extension = Path.GetExtension(filename).ToLowerInvariant();
        return extension is ".mp4" or ".m4v" or ".mov" or ".webm" ? extension : ".video";
    }
}
