using ListingStudio.Application.Authentication;
using ListingStudio.Application.Properties;
using ListingStudio.Infrastructure.Configuration;
using ListingStudio.Infrastructure.Identity;
using ListingStudio.Infrastructure.Persistence;
using ListingStudio.Infrastructure.Properties;
using ListingStudio.Infrastructure.Storage;
using ListingStudio.Application.Stories;
using ListingStudio.Infrastructure.Stories;
using ListingStudio.Application.Videos;
using ListingStudio.Infrastructure.Videos;
using ListingStudio.Application.Audio;
using ListingStudio.Infrastructure.Audio;
using ListingStudio.Application.Campaigns;
using ListingStudio.Infrastructure.Campaigns;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ListingStudio.Application.Billing;
using ListingStudio.Infrastructure.Billing;
using ListingStudio.Infrastructure.Health;

namespace ListingStudio.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<PostgreSqlOptions>()
            .Bind(configuration.GetSection(PostgreSqlOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.BuildConnectionString()),
                "A PostgreSQL connection string or complete host/database/username/password configuration is required.");
        services
            .AddOptions<AzureBlobStorageOptions>()
            .Bind(configuration.GetSection(AzureBlobStorageOptions.SectionName))
            .Validate(
                options => string.Equals(options.Provider, "Local", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(options.Provider, "Azure", StringComparison.OrdinalIgnoreCase),
                "AzureBlobStorage:Provider must be Local or Azure.")
            .Validate(
                options => !string.Equals(options.Provider, "Azure", StringComparison.OrdinalIgnoreCase)
                    || !string.IsNullOrWhiteSpace(options.ConnectionString)
                    || (Uri.TryCreate(options.ServiceUri, UriKind.Absolute, out var uri)
                        && uri.Scheme == Uri.UriSchemeHttps),
                "The Azure provider requires a connection string or an HTTPS service URI for managed identity.");
        services.AddOptions<StripeOptions>()
            .Bind(configuration.GetSection(StripeOptions.SectionName))
            .Validate(options => !options.Enabled
                || (Uri.TryCreate(options.ApiBaseUrl, UriKind.Absolute, out var uri)
                    && uri.Scheme == Uri.UriSchemeHttps
                    && Uri.TryCreate(options.PublicBaseUrl, UriKind.Absolute, out var publicUri)
                    && (publicUri.Scheme == Uri.UriSchemeHttps || publicUri.IsLoopback)
                    && !string.IsNullOrWhiteSpace(options.SecretKey)
                    && !string.IsNullOrWhiteSpace(options.WebhookSecret)
                    && !string.IsNullOrWhiteSpace(options.StarterPriceId)
                    && !string.IsNullOrWhiteSpace(options.ProfessionalPriceId)),
                "Enabled Stripe billing requires an HTTPS API URL, an HTTPS public base URL (HTTP is allowed only for loopback), secret key, webhook secret, and both price identifiers.")
            .Validate(options => options.StarterMonthlyCampaignAllowance >= 0
                    && options.ProfessionalMonthlyCampaignAllowance >= 0,
                "Stripe campaign allowances cannot be negative.")
            .Validate(options => options.WebhookToleranceSeconds is >= 60 and <= 900,
                "Stripe webhook tolerance must be between 60 and 900 seconds.");
        services.AddOptions<CampaignGenerationOptions>()
            .Bind(configuration.GetSection(CampaignGenerationOptions.SectionName))
            .Validate(options => options.PollIntervalSeconds is >= 1 and <= 60,
                "CampaignGeneration:PollIntervalSeconds must be between 1 and 60.")
            .Validate(options => options.StageTimeoutSeconds is >= 30 and <= 3_600,
                "CampaignGeneration:StageTimeoutSeconds must be between 30 and 3600.")
            .Validate(options => options.LeaseSeconds > options.StageTimeoutSeconds,
                "CampaignGeneration:LeaseSeconds must exceed StageTimeoutSeconds.");
        services.AddOptions<AddressLookupOptions>()
            .Bind(configuration.GetSection(AddressLookupOptions.SectionName))
            .Validate(options => !options.Enabled
                || (Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri)
                    && uri.Scheme == Uri.UriSchemeHttps),
                "Enabled address lookup requires an HTTPS base URL.")
            .Validate(options => options.MinimumQueryLength is >= 3 and <= 20,
                "AddressLookup:MinimumQueryLength must be between 3 and 20.")
            .Validate(options => options.MaximumSuggestions is >= 1 and <= 10,
                "AddressLookup:MaximumSuggestions must be between 1 and 10.")
            .Validate(options => options.RequestTimeoutSeconds is >= 2 and <= 30,
                "AddressLookup:RequestTimeoutSeconds must be between 2 and 30 seconds.");

        services.AddDbContext<ApplicationDbContext>((provider, options) =>
        {
            var postgreSql = provider.GetRequiredService<IOptions<PostgreSqlOptions>>().Value;
            options.UseNpgsql(
                postgreSql.BuildConnectionString(),
                npgsql => npgsql.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName));
        });

        services
            .AddIdentityCore<ApplicationUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 8;
                options.Password.RequireNonAlphanumeric = false;
                options.SignIn.RequireConfirmedAccount = false;
            })
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders();

        services
            .AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
                options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
                options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
            })
            .AddIdentityCookies();
        services.AddAuthorization();
        services.AddScoped<IAccountRegistrationService, AccountRegistrationService>();
        services.AddScoped<IPropertyService, PropertyService>();
        services.AddScoped<IPropertyMediaService, PropertyMediaService>();
        services.AddScoped<IPropertyMediaAnalysisProcessor, PropertyMediaAnalysisProcessor>();
        services.AddScoped<IPropertyStoryService, PropertyStoryService>();
        services.AddScoped<IVideoProductionPlanService, VideoProductionPlanService>();
        services.AddScoped<IVideoNarrationService, VideoNarrationService>();
        services.AddScoped<IGeneratedVideoClipService, GeneratedVideoClipService>();
        services.AddScoped<ICampaignGenerationService, CampaignGenerationService>();
        services.AddScoped<ICampaignGenerationProcessor, CampaignGenerationProcessor>();
        services.AddScoped<BillingService>();
        services.AddScoped<IBillingService>(provider => provider.GetRequiredService<BillingService>());
        services.AddScoped<IBillingUsageRecorder>(provider => provider.GetRequiredService<BillingService>());
        services.AddHealthChecks()
            .AddCheck<PostgreSqlHealthCheck>("postgresql", tags: ["ready"]);
        services.AddHttpClient<IBillingProviderGateway, StripeBillingGateway>((provider, client) =>
        {
            var stripe = provider.GetRequiredService<IOptions<StripeOptions>>().Value;
            client.BaseAddress = new Uri(stripe.ApiBaseUrl, UriKind.Absolute);
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddHttpClient<IAddressLookupService, ArcGisAddressLookupService>((provider, client) =>
        {
            var addressLookup = provider.GetRequiredService<IOptions<AddressLookupOptions>>().Value;
            client.BaseAddress = new Uri(addressLookup.BaseUrl, UriKind.Absolute);
            client.Timeout = TimeSpan.FromSeconds(addressLookup.RequestTimeoutSeconds);
        });
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IPropertyMediaStorage>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<AzureBlobStorageOptions>>();
            return options.Value.Provider.ToUpperInvariant() switch
            {
                "LOCAL" => ActivatorUtilities.CreateInstance<LocalPropertyMediaStorage>(provider),
                "AZURE" => ActivatorUtilities.CreateInstance<AzureBlobPropertyMediaStorage>(provider),
                _ => throw new InvalidOperationException("Unsupported property media storage provider."),
            };
        });
        services.AddSingleton<ICampaignAssetStorage>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<AzureBlobStorageOptions>>();
            return options.Value.Provider.ToUpperInvariant() switch
            {
                "LOCAL" => ActivatorUtilities.CreateInstance<LocalCampaignAssetStorage>(provider),
                "AZURE" => ActivatorUtilities.CreateInstance<AzureBlobCampaignAssetStorage>(provider),
                _ => throw new InvalidOperationException("Unsupported campaign asset storage provider."),
            };
        });

        return services;
    }
}
