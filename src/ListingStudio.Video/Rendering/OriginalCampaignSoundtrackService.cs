using System.Buffers.Binary;
using ListingStudio.Application.Videos;

namespace ListingStudio.Video.Rendering;

/// <summary>Creates deterministic, original instrumental beds so campaign music has no third-party licensing dependency.</summary>
public sealed class OriginalCampaignSoundtrackService : ICampaignSoundtrackService
{
    private const int SampleRate = 22_050;

    public async Task<VideoRenderMusicAsset?> MaterializeAsync(
        string? assetId,
        int durationMs,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        if (assetId is null)
        {
            return null;
        }

        var profile = assetId switch
        {
            CampaignCreativeDirector.HeritageWarmAssetId => new Profile(76, [261.63, 329.63, 392.00], 0.20),
            CampaignCreativeDirector.ModernAmbientAssetId => new Profile(92, [220.00, 277.18, 329.63], 0.16),
            CampaignCreativeDirector.UpbeatLifestyleAssetId => new Profile(112, [293.66, 369.99, 440.00], 0.18),
            _ => throw new InvalidDataException("The selected soundtrack is not part of the Listing Studio music library."),
        };

        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, $"soundtrack-{Math.Abs(assetId.GetHashCode(StringComparison.Ordinal))}.wav");
        await WriteWaveAsync(path, Math.Max(1_000, durationMs), profile, cancellationToken);
        return new VideoRenderMusicAsset(assetId, path);
    }

    private static async Task WriteWaveAsync(
        string path,
        int durationMs,
        Profile profile,
        CancellationToken cancellationToken)
    {
        var samples = checked((int)((long)SampleRate * durationMs / 1_000));
        var dataBytes = checked(samples * sizeof(short));
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920, true);
        var header = new byte[44];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), 36 + dataBytes);
        "WAVEfmt "u8.CopyTo(header.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), SampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(34), 16);
        "data"u8.CopyTo(header.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40), dataBytes);
        await stream.WriteAsync(header, cancellationToken);

        var buffer = new byte[16_384];
        var beatSeconds = 60d / profile.BeatsPerMinute;
        var chordSeconds = beatSeconds * 4;
        var written = 0;
        while (written < samples)
        {
            var batch = Math.Min(buffer.Length / 2, samples - written);
            for (var index = 0; index < batch; index++)
            {
                var time = (written + index) / (double)SampleRate;
                var chordStep = (int)(time / chordSeconds) % 4;
                var shift = chordStep switch { 1 => 4d / 3d, 2 => 3d / 2d, 3 => 5d / 4d, _ => 1d };
                var value = profile.Frequencies.Sum(frequency => Math.Sin(2 * Math.PI * frequency * shift * time)) / 3d;
                var pulse = Math.Exp(-8 * (time % beatSeconds) / beatSeconds) * Math.Sin(2 * Math.PI * 82 * time);
                var sample = (short)Math.Clamp((value * profile.Gain + pulse * 0.08) * short.MaxValue, short.MinValue, short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(index * 2), sample);
            }

            await stream.WriteAsync(buffer.AsMemory(0, batch * 2), cancellationToken);
            written += batch;
        }
    }

    private sealed record Profile(int BeatsPerMinute, double[] Frequencies, double Gain);
}
