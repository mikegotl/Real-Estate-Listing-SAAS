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
            CampaignCreativeDirector.HeritageWarmAssetId => new Profile(
                CampaignCreativeDirector.BeatsPerMinute(assetId), [261.63, 329.63, 392.00], 0.16, 0.45, false),
            CampaignCreativeDirector.ModernAmbientAssetId => new Profile(
                CampaignCreativeDirector.BeatsPerMinute(assetId), [220.00, 277.18, 329.63], 0.13, 0.75, true),
            CampaignCreativeDirector.UpbeatLifestyleAssetId => new Profile(
                CampaignCreativeDirector.BeatsPerMinute(assetId), [293.66, 369.99, 440.00], 0.14, 1.0, true),
            _ => throw new InvalidDataException("The selected soundtrack is not part of the Creative Listing Studio music library."),
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
        var written = 0;
        while (written < samples)
        {
            var batch = Math.Min(buffer.Length / 2, samples - written);
            for (var index = 0; index < batch; index++)
            {
                var sample = (short)(Math.Tanh(Sample(written + index, profile) * 1.2) * 0.85 * short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(index * 2), sample);
            }

            await stream.WriteAsync(buffer.AsMemory(0, batch * 2), cancellationToken);
            written += batch;
        }
    }

    /// <summary>
    /// A four-chord pad with a plucked bass on the chord root and a drum pattern locked to the tempo: a kick on every
    /// beat, a snare on two and four and a hi-hat on the eighths. Short-form edits cut on the same beat, so the
    /// cuts land on the drums.
    /// </summary>
    private static double Sample(int sampleIndex, Profile profile)
    {
        var time = sampleIndex / (double)SampleRate;
        var beatSeconds = 60d / profile.BeatsPerMinute;
        var beatIndex = (long)(time / beatSeconds);
        var inBeat = time - beatIndex * beatSeconds;
        var chordStep = (int)(beatIndex / 4 % 4);
        var shift = chordStep switch { 1 => 4d / 3d, 2 => 3d / 2d, 3 => 5d / 4d, _ => 1d };

        var pad = profile.Frequencies.Sum(frequency => Math.Sin(2 * Math.PI * frequency * shift * time)) / 3d
            * profile.PadGain;
        var bassFrequency = profile.Frequencies[0] / 2 * shift;
        var bassNote = profile.EighthBass ? inBeat % (beatSeconds / 2) : inBeat;
        var bass = Math.Sin(2 * Math.PI * bassFrequency * time) * Math.Exp(-5 * bassNote) * 0.22;

        var kickPhase = 2 * Math.PI * (48 * inBeat + 2.8 * (1 - Math.Exp(-28 * inBeat)));
        var kick = Math.Sin(kickPhase) * Math.Exp(-11 * inBeat) * 0.55;
        var snare = beatIndex % 2 == 1
            ? (Noise(sampleIndex) * 0.6 + Math.Sin(2 * Math.PI * 185 * inBeat) * 0.4) * Math.Exp(-18 * inBeat) * 0.32
            : 0;
        var inEighth = inBeat % (beatSeconds / 2);
        var hat = (Noise(sampleIndex) - Noise(sampleIndex - 1)) * Math.Exp(-70 * inEighth) * 0.07;

        return pad + bass + (kick + snare + hat) * profile.DrumLevel;
    }

    /// <summary>Deterministic white noise in [-1, 1], so the same soundtrack renders identically every time.</summary>
    private static double Noise(int sampleIndex)
    {
        var value = unchecked((uint)sampleIndex * 2_654_435_761u);
        value ^= value >> 15;
        value = unchecked(value * 2_246_822_519u);
        value ^= value >> 13;
        return value / (double)uint.MaxValue * 2 - 1;
    }

    private sealed record Profile(int BeatsPerMinute, double[] Frequencies, double PadGain, double DrumLevel, bool EighthBass);
}
