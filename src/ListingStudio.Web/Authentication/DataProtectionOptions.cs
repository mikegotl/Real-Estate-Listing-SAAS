namespace ListingStudio.Web.Authentication;

public sealed class DataProtectionOptions
{
    public const string SectionName = "DataProtection";

    public string ApplicationName { get; set; } = "ListingStudio.Web";

    public string BlobUri { get; set; } = string.Empty;

    public string KeyVaultKeyIdentifier { get; set; } = string.Empty;
}
