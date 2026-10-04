namespace ListingStudio.Infrastructure.Configuration;

public sealed class PostgreSqlOptions
{
    public const string SectionName = "PostgreSQL";
    public string ConnectionString { get; init; } = string.Empty;
}

public sealed class AzureBlobStorageOptions
{
    public const string SectionName = "AzureBlobStorage";
    public string Provider { get; init; } = "Local";
    public string ConnectionString { get; init; } = string.Empty;
    public string ContainerName { get; init; } = "property-media";
    public string LocalRootPath { get; init; } = "App_Data/property-media";
    public string CampaignLocalRootPath { get; init; } = "App_Data/campaign-assets";
}

public sealed class StripeOptions
{
    public const string SectionName = "Stripe";
    public string SecretKey { get; init; } = string.Empty;
    public string WebhookSecret { get; init; } = string.Empty;
}

public sealed class CampaignGenerationOptions
{
    public const string SectionName = "CampaignGeneration";
    public bool Enabled { get; init; } = true;
    public int PollIntervalSeconds { get; init; } = 2;
    public int StageTimeoutSeconds { get; init; } = 600;
    public int LeaseSeconds { get; init; } = 900;
}
