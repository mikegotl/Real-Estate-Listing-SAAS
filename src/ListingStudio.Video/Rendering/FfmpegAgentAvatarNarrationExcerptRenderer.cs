using System.Globalization;
using ListingStudio.Application.Videos;
using ListingStudio.Video.Configuration;
using ListingStudio.Video.Processing;
using Microsoft.Extensions.Options;

namespace ListingStudio.Video.Rendering;

public sealed class FfmpegAgentAvatarNarrationExcerptRenderer(IOptions<FfmpegOptions> options)
    : IAgentAvatarNarrationExcerptRenderer
{
    public async Task<AgentAvatarNarrationExcerpt> RenderAsync(
        Stream narration,
        string contentType,
        IReadOnlyList<AgentAvatarNarrationExcerptPart> parts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(narration);
        ArgumentNullException.ThrowIfNull(parts);
        if (!narration.CanRead || parts.Count is < 1 or > 2
            || parts.Any(part => part.StartMs < 0 || part.DurationMs <= 0)
            || parts.Sum(part => part.DurationMs) > 15_000)
        {
            throw new ArgumentException("The presenter narration excerpt is invalid.", nameof(parts));
        }

        var directory = Path.Combine(Path.GetTempPath(), $"listing-studio-avatar-audio-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var inputPath = Path.Combine(directory, $"narration{Extension(contentType)}");
            var outputPath = Path.Combine(directory, "presenter.mp3");
            await using (var target = new FileStream(inputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await narration.CopyToAsync(target, cancellationToken);
            }

            var filters = parts.Select((part, index) =>
                $"[0:a]atrim=start={Seconds(part.StartMs)}:end={Seconds(part.StartMs + part.DurationMs)},"
                + $"asetpts=PTS-STARTPTS[p{index}]").ToList();
            var inputs = string.Concat(Enumerable.Range(0, parts.Count).Select(index => $"[p{index}]"));
            filters.Add($"{inputs}concat=n={parts.Count}:v=0:a=1[out]");
            await FfmpegProcess.RunAsync(
                options.Value.ExecutablePath,
                directory,
                [
                    "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                    "-i", inputPath,
                    "-filter_complex", string.Join(';', filters),
                    "-map", "[out]", "-c:a", "libmp3lame", "-b:a", "128k", outputPath,
                ],
                120,
                cancellationToken);
            var audio = await File.ReadAllBytesAsync(outputPath, cancellationToken);
            return audio.Length > 0
                ? new AgentAvatarNarrationExcerpt(audio, "audio/mpeg", ".mp3", parts.Sum(part => part.DurationMs))
                : throw new InvalidDataException("The presenter narration excerpt is empty.");
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
        "audio/mpeg" => ".mp3",
        "audio/wav" or "audio/x-wav" => ".wav",
        _ => throw new InvalidDataException("The presenter narration has an unsupported content type."),
    };

    private static string Seconds(int milliseconds) =>
        (milliseconds / 1_000m).ToString("0.###", CultureInfo.InvariantCulture);
}
