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
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ListingStudio.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<PostgreSqlOptions>()
            .Bind(configuration.GetSection(PostgreSqlOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.ConnectionString), "A PostgreSQL connection string is required.");
        services
            .AddOptions<AzureBlobStorageOptions>()
            .Bind(configuration.GetSection(AzureBlobStorageOptions.SectionName))
            .Validate(
                options => string.Equals(options.Provider, "Local", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(options.Provider, "Azure", StringComparison.OrdinalIgnoreCase),
                "AzureBlobStorage:Provider must be Local or Azure.");
        services.AddOptions<StripeOptions>().Bind(configuration.GetSection(StripeOptions.SectionName));

        services.AddDbContext<ApplicationDbContext>((provider, options) =>
        {
            var postgreSql = provider.GetRequiredService<IOptions<PostgreSqlOptions>>().Value;
            options.UseNpgsql(
                postgreSql.ConnectionString,
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

        return services;
    }
}
