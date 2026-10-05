namespace ListingStudio.Infrastructure.Configuration;

using Npgsql;

public sealed class PostgreSqlOptions
{
    public const string SectionName = "PostgreSQL";
    public string ConnectionString { get; init; } = string.Empty;
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; } = 5432;
    public string Database { get; init; } = "listingstudio";
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;

    public string BuildConnectionString()
    {
        if (!string.IsNullOrWhiteSpace(ConnectionString))
        {
            return ConnectionString;
        }

        if (string.IsNullOrWhiteSpace(Host)
            || string.IsNullOrWhiteSpace(Database)
            || string.IsNullOrWhiteSpace(Username)
            || string.IsNullOrWhiteSpace(Password))
        {
            return string.Empty;
        }

        return new NpgsqlConnectionStringBuilder
        {
            Host = Host,
            Port = Port,
            Database = Database,
            Username = Username,
            Password = Password,
            SslMode = SslMode.VerifyFull,
            Timeout = 15,
            CommandTimeout = 30,
        }.ConnectionString;
    }
}

public sealed class AzureBlobStorageOptions
{
    public const string SectionName = "AzureBlobStorage";
    public string Provider { get; init; } = "Local";
    public string ConnectionString { get; init; } = string.Empty;
    public string ServiceUri { get; init; } = string.Empty;
    public string ContainerName { get; init; } = "property-media";
    public string LocalRootPath { get; init; } = "App_Data/property-media";
    public string CampaignLocalRootPath { get; init; } = "App_Data/campaign-assets";
}

public sealed class StripeOptions
{
    public const string SectionName = "Stripe";
    public bool Enabled { get; init; }
    public string ApiBaseUrl { get; init; } = "https://api.stripe.com";
    public string PublicBaseUrl { get; init; } = string.Empty;
    public string SecretKey { get; init; } = string.Empty;
    public string WebhookSecret { get; init; } = string.Empty;
    public string StarterPriceId { get; init; } = string.Empty;
    public string ProfessionalPriceId { get; init; } = string.Empty;
    public int StarterMonthlyCampaignAllowance { get; init; } = 4;
    public int ProfessionalMonthlyCampaignAllowance { get; init; } = 12;
    public int WebhookToleranceSeconds { get; init; } = 300;
}

public sealed class CampaignGenerationOptions
{
    public const string SectionName = "CampaignGeneration";
    public bool Enabled { get; init; } = true;
    public int PollIntervalSeconds { get; init; } = 2;
    public int StageTimeoutSeconds { get; init; } = 600;
    public int LeaseSeconds { get; init; } = 900;
}
