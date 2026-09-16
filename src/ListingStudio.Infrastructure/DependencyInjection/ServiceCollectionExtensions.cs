using ListingStudio.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ListingStudio.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PostgreSqlOptions>().Bind(configuration.GetSection(PostgreSqlOptions.SectionName));
        services.AddOptions<AzureBlobStorageOptions>().Bind(configuration.GetSection(AzureBlobStorageOptions.SectionName));
        services.AddOptions<StripeOptions>().Bind(configuration.GetSection(StripeOptions.SectionName));
        return services;
    }
}
