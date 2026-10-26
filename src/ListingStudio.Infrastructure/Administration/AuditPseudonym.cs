using System.Security.Cryptography;
using System.Text;

namespace ListingStudio.Infrastructure.Administration;

/// <summary>The one-way reference the platform audit log stores in place of a user id.</summary>
public static class AuditPseudonym
{
    public static string For(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
