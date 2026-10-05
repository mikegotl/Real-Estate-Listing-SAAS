namespace ListingStudio.Web.Authentication;

public sealed class GoogleExternalLoginOptions
{
    public const string SectionName = "Authentication:Google";

    public bool Enabled { get; set; }

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;
}

public sealed class AppleExternalLoginOptions
{
    public const string SectionName = "Authentication:Apple";

    public bool Enabled { get; set; }

    public string ClientId { get; set; } = string.Empty;

    public string TeamId { get; set; } = string.Empty;

    public string KeyId { get; set; } = string.Empty;

    public string PrivateKey { get; set; } = string.Empty;
}
