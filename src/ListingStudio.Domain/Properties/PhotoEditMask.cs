using System.Globalization;

namespace ListingStudio.Domain.Properties;

/// <summary>A normalized rectangle the agent explicitly allows an AI photo edit to change.</summary>
public readonly record struct PhotoEditRegion(decimal X, decimal Y, decimal Width, decimal Height)
{
    public decimal Area => Width * Height;

    public void Validate()
    {
        if (X is < 0 or >= 1 || Y is < 0 or >= 1
            || Width is <= 0 or > 1 || Height is <= 0 or > 1
            || X + Width > 1 || Y + Height > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(PhotoEditRegion), "Photo edit regions must lie inside the photo.");
        }
    }
}

/// <summary>
/// Compact persistence format for the regions an agent painted over. The overall area is deliberately bounded so a
/// clutter request cannot become permission to regenerate most of a listing photo.
/// </summary>
public static class PhotoEditMask
{
    public const int MaximumRegions = 40;
    public const int MaximumStoredLength = 2_000;
    public const decimal MaximumSelectedArea = 0.35m;

    public static string Encode(IReadOnlyCollection<PhotoEditRegion> regions)
    {
        ArgumentNullException.ThrowIfNull(regions);
        Validate(regions);
        return string.Join('|', regions.Select(region => string.Join(',',
            Format(region.X), Format(region.Y), Format(region.Width), Format(region.Height))));
    }

    public static IReadOnlyList<PhotoEditRegion> Decode(string encoded)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encoded);
        if (encoded.Length > MaximumStoredLength)
        {
            throw new ArgumentException("The photo edit selection is too large.", nameof(encoded));
        }

        var regions = encoded.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value =>
            {
                var parts = value.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length != 4)
                {
                    throw new ArgumentException("The photo edit selection is invalid.", nameof(encoded));
                }

                return new PhotoEditRegion(
                    Parse(parts[0]), Parse(parts[1]), Parse(parts[2]), Parse(parts[3]));
            })
            .ToArray();
        Validate(regions);
        return regions;
    }

    private static void Validate(IReadOnlyCollection<PhotoEditRegion> regions)
    {
        if (regions.Count is 0 or > MaximumRegions)
        {
            throw new ArgumentOutOfRangeException(nameof(regions), $"Choose between 1 and {MaximumRegions} clutter areas.");
        }

        foreach (var region in regions)
        {
            region.Validate();
        }

        if (regions.Sum(region => region.Area) > MaximumSelectedArea)
        {
            throw new ArgumentOutOfRangeException(nameof(regions), "Select less than 35% of the photo for clutter removal.");
        }
    }

    private static string Format(decimal value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static decimal Parse(string value) => decimal.TryParse(
        value,
        NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
        CultureInfo.InvariantCulture,
        out var parsed)
            ? parsed
            : throw new ArgumentException("The photo edit selection is invalid.");
}
