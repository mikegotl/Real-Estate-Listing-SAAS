using ListingStudio.Domain.Properties;

namespace ListingStudio.Web.Components.Properties;

/// <summary>How the Media tab names photo fixes.</summary>
public static class PhotoFixText
{
    public static string Describe(PhotoFix fixes)
    {
        var names = new List<string>(2);
        if (fixes.HasFlag(PhotoFix.BrightnessAndColor))
        {
            names.Add("brightness and color");
        }

        if (fixes.HasFlag(PhotoFix.RemoveClutter))
        {
            names.Add("clutter removed");
        }

        return names.Count == 0 ? "no fixes" : string.Join(", ", names);
    }

    public static string InProgress(PhotoFix fix) => fix == PhotoFix.RemoveClutter
        ? "Removing clutter…"
        : "Fixing brightness and color…";
}
