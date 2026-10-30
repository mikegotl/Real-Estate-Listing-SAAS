using Microsoft.AspNetCore.DataProtection;

namespace ListingStudio.Infrastructure.Administration;

public static class TesterInvitationEmailProtection
{
    public const string Purpose = "CreativeListingStudio.TesterInvitationEmail.v1";

    public static IDataProtector CreateProtector(IDataProtectionProvider provider) =>
        provider.CreateProtector(Purpose);
}
