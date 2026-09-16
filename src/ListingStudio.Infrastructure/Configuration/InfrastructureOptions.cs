namespace ListingStudio.Infrastructure.Configuration;

public sealed class PostgreSqlOptions
{
    public const string SectionName = "PostgreSQL";
    public string ConnectionString { get; init; } = string.Empty;
}

public sealed class AzureBlobStorageOptions
{
    public const string SectionName = "AzureBlobStorage";
    public string ConnectionString { get; init; } = string.Empty;
    public string ContainerName { get; init; } = string.Empty;
}

public sealed class StripeOptions
{
    public const string SectionName = "Stripe";
    public string SecretKey { get; init; } = string.Empty;
    public string WebhookSecret { get; init; } = string.Empty;
}
