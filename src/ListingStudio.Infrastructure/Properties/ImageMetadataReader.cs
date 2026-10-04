using System.Buffers.Binary;

namespace ListingStudio.Infrastructure.Properties;

internal static class ImageMetadataReader
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static ImageMetadata Read(ReadOnlySpan<byte> data)
    {
        if (TryReadPng(data, out var metadata)
            || TryReadJpeg(data, out metadata)
            || TryReadWebP(data, out metadata))
        {
            return metadata;
        }

        throw new InvalidDataException("The file content is not a supported JPG, PNG, or WEBP image.");
    }

    private static bool TryReadPng(ReadOnlySpan<byte> data, out ImageMetadata metadata)
    {
        metadata = default;
        if (data.Length < 24
            || !data[..8].SequenceEqual(PngSignature)
            || !data.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            return false;
        }

        var width = BinaryPrimitives.ReadInt32BigEndian(data.Slice(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(data.Slice(20, 4));
        metadata = Valid("PNG", width, height);
        return true;
    }

    private static bool TryReadJpeg(ReadOnlySpan<byte> data, out ImageMetadata metadata)
    {
        metadata = default;
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
        {
            return false;
        }

        var offset = 2;
        while (offset + 4 <= data.Length)
        {
            while (offset < data.Length && data[offset] != 0xFF)
            {
                offset++;
            }

            while (offset < data.Length && data[offset] == 0xFF)
            {
                offset++;
            }

            if (offset >= data.Length)
            {
                break;
            }

            var marker = data[offset++];
            if (marker is 0xD8 or 0xD9)
            {
                continue;
            }

            if (marker == 0xDA || offset + 2 > data.Length)
            {
                break;
            }

            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
            if (segmentLength < 2 || offset + segmentLength > data.Length)
            {
                break;
            }

            if (IsStartOfFrame(marker) && segmentLength >= 7)
            {
                var height = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 3, 2));
                var width = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 5, 2));
                metadata = Valid("JPEG", width, height);
                return true;
            }

            offset += segmentLength;
        }

        throw new InvalidDataException("The JPEG image does not contain valid dimensions.");
    }

    private static bool TryReadWebP(ReadOnlySpan<byte> data, out ImageMetadata metadata)
    {
        metadata = default;
        if (data.Length < 30
            || !data[..4].SequenceEqual("RIFF"u8)
            || !data.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return false;
        }

        var chunk = data.Slice(12, 4);
        if (chunk.SequenceEqual("VP8X"u8))
        {
            var width = 1 + ReadUInt24LittleEndian(data.Slice(24, 3));
            var height = 1 + ReadUInt24LittleEndian(data.Slice(27, 3));
            metadata = Valid("WEBP", width, height);
            return true;
        }

        if (chunk.SequenceEqual("VP8L"u8) && data[20] == 0x2F)
        {
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(21, 4));
            var width = 1 + (int)(bits & 0x3FFF);
            var height = 1 + (int)((bits >> 14) & 0x3FFF);
            metadata = Valid("WEBP", width, height);
            return true;
        }

        if (chunk.SequenceEqual("VP8 "u8)
            && data.Length >= 30
            && data.Slice(23, 3).SequenceEqual(new byte[] { 0x9D, 0x01, 0x2A }))
        {
            var width = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(26, 2)) & 0x3FFF;
            var height = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(28, 2)) & 0x3FFF;
            metadata = Valid("WEBP", width, height);
            return true;
        }

        throw new InvalidDataException("The WEBP image does not contain valid dimensions.");
    }

    private static bool IsStartOfFrame(byte marker) => marker is
        0xC0 or 0xC1 or 0xC2 or 0xC3 or
        0xC5 or 0xC6 or 0xC7 or
        0xC9 or 0xCA or 0xCB or
        0xCD or 0xCE or 0xCF;

    private static int ReadUInt24LittleEndian(ReadOnlySpan<byte> data) =>
        data[0] | (data[1] << 8) | (data[2] << 16);

    private static ImageMetadata Valid(string format, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException("Image dimensions must be positive.");
        }

        return new ImageMetadata(format, width, height);
    }
}

internal readonly record struct ImageMetadata(string Format, int Width, int Height);
