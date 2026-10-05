using AspNet.Security.OAuth.Apple;
using Microsoft.AspNetCore.Authentication.Google;

namespace ListingStudio.Web.Authentication;

public static class ExternalLoginServiceCollectionExtensions
{
    public static IServiceCollection AddExternalLoginProviders(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var authentication = services.AddAuthentication();
        var google = configuration.GetSection(GoogleExternalLoginOptions.SectionName)
            .Get<GoogleExternalLoginOptions>() ?? new GoogleExternalLoginOptions();

        if (google.Enabled)
        {
            Require(google.ClientId, "Authentication:Google:ClientId");
            Require(google.ClientSecret, "Authentication:Google:ClientSecret");
            authentication.AddGoogle(GoogleDefaults.AuthenticationScheme, "Google", options =>
            {
                options.ClientId = google.ClientId;
                options.ClientSecret = google.ClientSecret;
                options.AccessDeniedPath = "/Account/Login";
            });
        }

        var apple = configuration.GetSection(AppleExternalLoginOptions.SectionName)
            .Get<AppleExternalLoginOptions>() ?? new AppleExternalLoginOptions();

        if (apple.Enabled)
        {
            Require(apple.ClientId, "Authentication:Apple:ClientId");
            Require(apple.TeamId, "Authentication:Apple:TeamId");
            Require(apple.KeyId, "Authentication:Apple:KeyId");
            Require(apple.PrivateKey, "Authentication:Apple:PrivateKey");

            var privateKey = apple.PrivateKey.Replace("\\n", "\n", StringComparison.Ordinal);
            authentication.AddApple(AppleAuthenticationDefaults.AuthenticationScheme, "Apple", options =>
            {
                options.ClientId = apple.ClientId;
                options.TeamId = apple.TeamId;
                options.KeyId = apple.KeyId;
                options.GenerateClientSecret = true;
                options.AccessDeniedPath = "/Account/Login";
                options.PrivateKey = (_, _) =>
                    Task.FromResult<ReadOnlyMemory<char>>(privateKey.AsMemory());
            });
        }

        return services;
    }

    private static void Require(string value, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{key} is required when its external login provider is enabled.");
        }
    }
}
