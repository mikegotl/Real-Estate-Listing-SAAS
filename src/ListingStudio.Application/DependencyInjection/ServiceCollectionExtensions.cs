using Microsoft.Extensions.DependencyInjection;
using ListingStudio.Application.Stories;

namespace ListingStudio.Application.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<IPropertyStoryGroundingValidator, PropertyStoryGroundingValidator>();
        return services;
    }
}
