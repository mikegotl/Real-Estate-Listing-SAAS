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
using ListingStudio.Application.Neighborhoods;
using ListingStudio.Infrastructure.Neighborhoods;
using ListingStudio.Application.Notifications;
using ListingStudio.Infrastructure.Notifications;
using ListingStudio.Application.Administration;
using ListingStudio.Infrastructure.Administration;
using ListingStudio.Application.Leads;
using ListingStudio.Infrastructure.Leads;
using ListingStudio.Application.Feedback;
using ListingStudio.Infrastructure.Feedback;
using ListingStudio.Application.Email;
using ListingStudio.Infrastructure.Email;
using ListingStudio.Application.Help;
using ListingStudio.Infrastructure.Help;

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
                    && !string.IsNullOrWhiteSpace(options.BasicPriceId)
                    && !string.IsNullOrWhiteSpace(options.ProfessionalPriceId)
                    && !string.IsNullOrWhiteSpace(options.EnterprisePriceId)),
                "Enabled Stripe billing requires an HTTPS API URL, an HTTPS public base URL (HTTP is allowed only for loopback), secret key, webhook secret, and the Basic, Professional and Enterprise price identifiers.")
            .Validate(options => options.FreeTrialCampaignAllowance >= 0
                    && options.FreeTrialDays is >= 1 and <= 90
                    && options.BasicMonthlyCampaignAllowance >= 0
                    && options.ProfessionalMonthlyCampaignAllowance >= 0
                    && options.EnterpriseMonthlyCampaignAllowance >= 0,
                "Stripe campaign allowances cannot be negative.")
            .Validate(options => options.BasicMonthlyPriceUsd > 0
                    && options.ProfessionalMonthlyPriceUsd > options.BasicMonthlyPriceUsd
                    && options.EnterpriseMonthlyPriceUsd > options.ProfessionalMonthlyPriceUsd,
                "Stripe display prices must be positive and rise from Basic to Professional to Enterprise.")
            .Validate(options => options.AnnualDiscountPercent is >= 0 and < 100,
                "Stripe:AnnualDiscountPercent must be between 0 and 99.")
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
        services.AddOptions<NeighborhoodInsightsOptions>()
            .Bind(configuration.GetSection(NeighborhoodInsightsOptions.SectionName))
            .Validate(options => !options.Enabled
                || (Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri)
                    && uri.Scheme == Uri.UriSchemeHttps
                    && !string.IsNullOrWhiteSpace(options.ApiKey)),
                "Enabled Neighborhood Insights requires an HTTPS Google Places URL and API key.")
            .Validate(options => options.RadiusMeters is >= 100 and <= 50_000,
                "NeighborhoodInsights:RadiusMeters must be between 100 and 50000.")
            .Validate(options => options.MaximumResults is >= 1 and <= 20,
                "NeighborhoodInsights:MaximumResults must be between 1 and 20.")
            .Validate(options => options.RequestTimeoutSeconds is >= 2 and <= 60,
                "NeighborhoodInsights:RequestTimeoutSeconds must be between 2 and 60 seconds.")
            .Validate(options => options.PlaceTypes.Length is >= 1 and <= 50,
                "NeighborhoodInsights:PlaceTypes must contain between 1 and 50 supported place types.");
        services.AddOptions<SmtpEmailOptions>()
            .Bind(configuration.GetSection(SmtpEmailOptions.SectionName))
            .Validate(options => options.Port is > 0 and <= 65_535,
                "Email:Smtp:Port must be a valid port.")
            .Validate(options => options.TimeoutSeconds is >= 5 and <= 120,
                "Email:Smtp:TimeoutSeconds must be between 5 and 120.")
            .Validate(options => string.IsNullOrWhiteSpace(options.FromAddress)
                    || MimeKit.MailboxAddress.TryParse(options.FromAddress, out _),
                "Email:Smtp:FromAddress must be an email address.");
        services.AddOptions<FeatureSuggestionEmailOptions>()
            .Bind(configuration.GetSection(FeatureSuggestionEmailOptions.SectionName))
            .Validate(options => string.IsNullOrWhiteSpace(options.AppBaseUrl)
                    || Uri.TryCreate(options.AppBaseUrl, UriKind.Absolute, out _),
                "Email:Comments:AppBaseUrl must be an absolute URL.");
        services.AddOptions<TesterInvitationOptions>()
            .Bind(configuration.GetSection(TesterInvitationOptions.SectionName))
            .Validate(options => Uri.TryCreate(options.AppBaseUrl, UriKind.Absolute, out var uri)
                    && (uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback),
                "TesterInvitations:AppBaseUrl must be an HTTPS URL (HTTP is allowed only for loopback).");

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
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders();
        services.AddScoped<IUserClaimsPrincipalFactory<ApplicationUser>, ListingStudioUserClaimsPrincipalFactory>();

        services
            .AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
                options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
                options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
            })
            .AddIdentityCookies();
        services.AddAuthorization(options => options.AddPolicy(
            PlatformAdministration.Policy,
            policy => policy.RequireAuthenticatedUser().RequireRole(PlatformAdministration.Role)));
        services.AddScoped<IPlatformAdminDashboardService, PlatformAdminDashboardService>();
        services.AddScoped<IPlatformAdminMetricsResetService, PlatformMetricsResetService>();
        services.AddScoped<PlatformAdminRoleBootstrapper>();
        services.AddScoped<IPlatformAdminAccountDirectory, PlatformAdminAccountDirectory>();
        services.AddScoped<IPlatformAdminImpersonationAudit, PlatformAdminImpersonationAudit>();
        services.AddScoped<IPlatformAdminTesterAccess, PlatformAdminTesterAccess>();
        services.AddScoped<IPlatformAdminTesterInvitationService, PlatformAdminTesterInvitationService>();
        services.AddScoped<IAdminUserEmailService, AdminUserEmailService>();
        services.AddScoped<IAdminUserEmailProcessor, AdminUserEmailProcessor>();
        // Identity rebuilds the cookie principal when it revalidates the security stamp; keep the impersonation
        // claims so an admin session does not silently turn into a plain customer session.
        services.Configure<SecurityStampValidatorOptions>(options => options.OnRefreshingPrincipal = context =>
        {
            var impersonationClaims = context.CurrentPrincipal?.Claims
                .Where(claim => claim.Type is PlatformAdminImpersonation.ImpersonatorUserIdClaim
                    or PlatformAdminImpersonation.ImpersonatorEmailClaim)
                .Select(claim => new System.Security.Claims.Claim(claim.Type, claim.Value))
                .ToList() ?? [];
            if (impersonationClaims.Count > 0 && context.NewPrincipal?.Identity is System.Security.Claims.ClaimsIdentity identity)
            {
                identity.AddClaims(impersonationClaims);
            }

            return Task.CompletedTask;
        });
        services.AddSingleton<OperationalEventRecorder>();
        services.AddSingleton<IAiUsagePropertyContext, AiUsagePropertyContext>();
        services.AddSingleton<IOperationalEventRecorder>(provider =>
            provider.GetRequiredService<OperationalEventRecorder>());
        services.AddSingleton<IAiUsageRecorder>(provider =>
            provider.GetRequiredService<OperationalEventRecorder>());
        services.AddScoped<IAccountRegistrationService, AccountRegistrationService>();
        services.AddScoped<IPropertyService, PropertyService>();
        services.AddScoped<IPropertyListingSourceService, PropertyListingSourceService>();
        services.AddScoped<IPropertyMediaService, PropertyMediaService>();
        services.AddScoped<IRoomNarrationService, RoomNarrationService>();
        services.AddScoped<IPropertyMediaResetService, PropertyMediaResetService>();
        services.AddScoped<IPropertyMediaAnalysisProcessor, PropertyMediaAnalysisProcessor>();
        services.AddScoped<IPropertyVideoService, PropertyVideoService>();
        services.AddScoped<IPropertyVideoProcessingProcessor, PropertyVideoProcessingProcessor>();
        services.AddScoped<IPropertyVideoFrameExtractionProcessor, PropertyVideoFrameExtractionProcessor>();
        services.AddScoped<IPropertyNarrationScriptService, PropertyNarrationScriptService>();
        services.AddScoped<IPropertyStoryService, PropertyStoryService>();
        services.AddScoped<INeighborhoodInsightService, NeighborhoodInsightService>();
        services.AddScoped<IVideoProductionPlanService, VideoProductionPlanService>();
        services.AddScoped<IVideoNarrationService, VideoNarrationService>();
        services.AddScoped<IGeneratedVideoClipService, GeneratedVideoClipService>();
        services.AddScoped<IAgentAvatarVideoService, AgentAvatarVideoService>();
        services.AddScoped<IWalkthroughTransitionService, WalkthroughTransitionService>();
        services.AddScoped<IWalkthroughTransitionProcessor, WalkthroughTransitionProcessor>();
        services.AddScoped<IAerialIntroService, AerialIntroService>();
        services.AddScoped<IAerialIntroProcessor, AerialIntroProcessor>();
        services.AddScoped<IPropertyPhotoFixService, PropertyPhotoFixService>();
        services.AddScoped<IPropertyPhotoFixProcessor, PropertyPhotoFixProcessor>();
        services.AddScoped<ICampaignGenerationService, CampaignGenerationService>();
        services.AddScoped<ICampaignGenerationProcessor, CampaignGenerationProcessor>();
        services.AddScoped<ICampaignVideoChatService, CampaignVideoChatService>();
        services.AddScoped<IHelpChatService, HelpChatService>();
        services.AddScoped<IAgentPortraitService, AgentPortraitService>();
        services.AddScoped<IAgentProfileService, AgentProfileService>();
        services.AddScoped<ICampaignSettingsPresetService, CampaignSettingsPresetService>();
        services.AddScoped<IAgentAvatarPreviewService, AgentAvatarPreviewService>();
        services.AddScoped<IUserNotificationService, UserNotificationService>();
        services.AddScoped<ILeadGenerationService, LeadGenerationService>();
        services.AddScoped<IFeatureSuggestionService, FeatureSuggestionService>();
        services.AddScoped<IFeatureSuggestionEmailProcessor, FeatureSuggestionEmailProcessor>();
        services.AddSingleton<IOutboundEmailSender, SmtpOutboundEmailSender>();
        services.AddScoped<BillingService>();
        services.AddScoped<IBillingService>(provider => provider.GetRequiredService<BillingService>());
        services.AddScoped<ICampaignBillingGate>(provider => provider.GetRequiredService<BillingService>());
        services.AddScoped<IPlanFeatureGate>(provider => provider.GetRequiredService<BillingService>());
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
        services.AddHttpClient<INeighborhoodDataProvider, GooglePlacesNeighborhoodDataProvider>((provider, client) =>
        {
            var neighborhood = provider.GetRequiredService<IOptions<NeighborhoodInsightsOptions>>().Value;
            client.BaseAddress = new Uri(neighborhood.BaseUrl, UriKind.Absolute);
            client.Timeout = TimeSpan.FromSeconds(neighborhood.RequestTimeoutSeconds);
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
