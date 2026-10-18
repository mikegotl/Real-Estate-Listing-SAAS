using System.Globalization;

namespace ListingStudio.Web.Components.Layout;

public static class NotificationTime
{
    /// <summary>"Just now", "5 min ago", "3 hr ago", "Yesterday", then the date.</summary>
    public static string Describe(DateTimeOffset createdAtUtc, DateTimeOffset nowUtc)
    {
        var age = nowUtc - createdAtUtc;
        if (age < TimeSpan.FromMinutes(1))
        {
            return "Just now";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"{(int)age.TotalMinutes} min ago";
        }

        if (age < TimeSpan.FromHours(24))
        {
            return $"{(int)age.TotalHours} hr ago";
        }

        return age < TimeSpan.FromHours(48)
            ? "Yesterday"
            : createdAtUtc.ToString("MMM d", CultureInfo.InvariantCulture);
    }
}
