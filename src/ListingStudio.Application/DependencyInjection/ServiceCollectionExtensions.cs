using Microsoft.Extensions.DependencyInjection;

namespace ListingStudio.Application.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services) => services;
}
