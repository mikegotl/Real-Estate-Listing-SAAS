using System.Security.Claims;
using ListingStudio.AI.DependencyInjection;
using ListingStudio.Application.Properties;
using ListingStudio.Application.DependencyInjection;
using ListingStudio.Application.Campaigns;
using ListingStudio.Application.Videos;
using ListingStudio.Application.Billing;
using ListingStudio.Domain.Billing;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Infrastructure.DependencyInjection;
using ListingStudio.Infrastructure.Identity;
using ListingStudio.Video.DependencyInjection;
using ListingStudio.Web;
using ListingStudio.Web.Components;
using ListingStudio.Web.Components.Properties;
using ListingStudio.Web.Components.Account;
using ListingStudio.Web.Authentication;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using MudBlazor.Services;
using Microsoft.AspNetCore.Antiforgery;
using System.Text;
using System.Text.Json;
using ListingStudio.Infrastructure.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ListingStudio.Application.Neighborhoods;
using ListingStudio.Application.Administration;
using ListingStudio.Web.Administration;
using ListingStudio.Application.Leads;
using QRCoder;

var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsProduction())
{
    builder.Logging.ClearProviders();
    builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
}

if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
    var managedIdentityClientId = builder.Configuration["AZURE_CLIENT_ID"];
    var credential = string.IsNullOrWhiteSpace(managedIdentityClientId)
        ? new DefaultAzureCredential()
        : new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ManagedIdentityClientId = managedIdentityClientId,
        });
    builder.Services.AddOpenTelemetry().UseAzureMonitor(options => options.Credential = credential);
}

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddMudServices();
builder.Services.AddListingStudioDataProtection(builder.Configuration, builder.Environment);
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();
builder.Services.AddScoped<PhotoAssistantSelectionState>();
builder.Services.AddSingleton<DevelopmentEmailStore>();
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"]);
builder.Services.AddScoped<IEmailSender<ApplicationUser>, DevelopmentIdentityEmailSender>();
builder.Services.Configure<MediaAnalysisOptions>(builder.Configuration.GetSection(MediaAnalysisOptions.SectionName));
builder.Services.Configure<SiteOptions>(builder.Configuration.GetSection(SiteOptions.SectionName));
builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration)
    .AddAI(builder.Configuration)
    .AddVideo(builder.Configuration);
builder.Services.AddExternalLoginProviders(builder.Configuration);

var app = builder.Build();

if (await PlatformAdminCommand.TryExecuteAsync(args, app.Services))
{
    return;
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/status/{0}", createScopeForStatusCodePages: true);

app.UseHttpsRedirection();
// Listings are U.S. properties priced in dollars, so formatting must not depend on the host locale or browser language.
app.UseRequestLocalization(new RequestLocalizationOptions()
    .SetDefaultCulture("en-US")
    .AddSupportedCultures("en-US")
    .AddSupportedUICultures("en-US"));
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<RequiredPasswordChangeMiddleware>();
app.UseMiddleware<OperationalTelemetryMiddleware>();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("live"),
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready"),
});
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.MapGet("/api/admin/dashboard", async (
    int? days,
    IPlatformAdminDashboardService dashboardService,
    TimeProvider timeProvider,
    CancellationToken cancellationToken) =>
{
    var selectedDays = days is 1 or 7 or 30 or 90 ? days.Value : 7;
    return Results.Ok(await dashboardService.GetAsync(
        AdminDateRange.LastDays(selectedDays, timeProvider),
        cancellationToken));
}).RequireAuthorization(PlatformAdministration.Policy);

app.MapPost("/Account/ExternalLogin", async (
    HttpContext context,
    IAntiforgery antiforgery,
    SignInManager<ApplicationUser> signInManager,
    CancellationToken cancellationToken) =>
{
    await antiforgery.ValidateRequestAsync(context);
    var form = await context.Request.ReadFormAsync(cancellationToken);
    var provider = form["provider"].ToString();
    var schemes = await signInManager.GetExternalAuthenticationSchemesAsync();
    if (!schemes.Any(scheme => string.Equals(scheme.Name, provider, StringComparison.Ordinal)))
    {
        return Results.BadRequest("Unknown external login provider.");
    }

    var returnUrl = LocalReturnUrl(form["returnUrl"]);
    var callbackUrl = $"/Account/ExternalLoginCallback?returnUrl={Uri.EscapeDataString(returnUrl)}";
    var properties = signInManager.ConfigureExternalAuthenticationProperties(provider, callbackUrl);
    return Results.Challenge(properties, [provider]);
});

app.MapPost("/Account/Logout", async (
    SignInManager<ApplicationUser> signInManager,
    IPlatformAdminImpersonationAudit impersonationAudit,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    var impersonatorId = PlatformAdminImpersonation.ImpersonatorUserId(context.User);
    var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (impersonatorId is not null && userId is not null)
    {
        await impersonationAudit.RecordStopAsync(impersonatorId, userId, "signed-out", cancellationToken);
    }

    await signInManager.SignOutAsync();
    return Results.LocalRedirect("~/");
}).RequireAuthorization();

app.MapPost("/admin/impersonation/start", async (
    HttpContext context,
    IAntiforgery antiforgery,
    IPlatformAdminImpersonationAudit impersonationAudit,
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    TimeProvider timeProvider,
    CancellationToken cancellationToken) =>
{
    await antiforgery.ValidateRequestAsync(context);
    var administratorId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (administratorId is null)
    {
        return Results.Unauthorized();
    }

    var form = await context.Request.ReadFormAsync(cancellationToken);
    var targetUserId = form["userId"].ToString();
    var searchQuery = form["q"].ToString();
    if (string.IsNullOrWhiteSpace(targetUserId))
    {
        return Results.BadRequest("A user is required.");
    }

    var result = await impersonationAudit.AuthorizeStartAsync(administratorId, targetUserId, cancellationToken);
    var target = result.Succeeded ? await userManager.FindByIdAsync(result.Target!.UserId) : null;
    var administrator = await userManager.FindByIdAsync(administratorId);
    if (target is null || administrator is null)
    {
        return Results.LocalRedirect(
            $"/admin/users?q={Uri.EscapeDataString(searchQuery)}&error={result.Failure}");
    }

    // A short, non-renewable session cookie: closing the browser or waiting an hour ends the impersonation.
    await signInManager.SignInWithClaimsAsync(
        target,
        new AuthenticationProperties
        {
            IsPersistent = false,
            AllowRefresh = false,
            ExpiresUtc = timeProvider.GetUtcNow().Add(PlatformAdminImpersonation.MaximumDuration),
        },
        [
            new Claim(PlatformAdminImpersonation.ImpersonatorUserIdClaim, administrator.Id),
            new Claim(
                PlatformAdminImpersonation.ImpersonatorEmailClaim,
                administrator.Email ?? administrator.UserName ?? "platform administrator"),
        ]);
    return Results.LocalRedirect(LocalReturnUrl(form["returnUrl"]));
}).RequireAuthorization(PlatformAdministration.Policy);

app.MapPost("/admin/users/testers", async (
    HttpContext context,
    IAntiforgery antiforgery,
    IPlatformAdminTesterAccess testerAccess,
    CancellationToken cancellationToken) =>
{
    await antiforgery.ValidateRequestAsync(context);
    var administratorId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (administratorId is null)
    {
        return Results.Unauthorized();
    }

    var form = await context.Request.ReadFormAsync(cancellationToken);
    var targetUserId = form["userId"].ToString();
    if (string.IsNullOrWhiteSpace(targetUserId) || !bool.TryParse(form["isTester"], out var isTester))
    {
        return Results.BadRequest("A user and a Testers choice are required.");
    }

    var result = await testerAccess.SetTesterAsync(administratorId, targetUserId, isTester, cancellationToken);
    var notice = result.Failure switch
    {
        TesterChangeFailure.None when !result.Changed => "tester-unchanged",
        TesterChangeFailure.None when isTester => result.WelcomeEmailQueued ? "tester-added-emailed" : "tester-added",
        TesterChangeFailure.None => "tester-removed",
        _ => $"tester-{result.Failure}",
    };
    var returnQuery = string.Equals(form["testers"], "true", StringComparison.OrdinalIgnoreCase)
        ? "testers=true"
        : $"q={Uri.EscapeDataString(form["q"].ToString())}";
    return Results.LocalRedirect($"/admin/users?{returnQuery}&notice={notice}");
}).RequireAuthorization(PlatformAdministration.Policy);

app.MapPost("/admin/users/testers/invite", async (
    HttpContext context,
    IAntiforgery antiforgery,
    IPlatformAdminTesterInvitationService invitationService,
    CancellationToken cancellationToken) =>
{
    await antiforgery.ValidateRequestAsync(context);
    var administratorId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (administratorId is null)
    {
        return Results.Unauthorized();
    }

    var form = await context.Request.ReadFormAsync(cancellationToken);
    var result = await invitationService.InviteAsync(
        administratorId,
        form["email"].ToString(),
        cancellationToken);
    var notice = result.Failure switch
    {
        TesterInvitationFailure.None when result.AccountCreated => "tester-invited-new",
        TesterInvitationFailure.None when result.TesterAccessAdded => "tester-invited-existing",
        TesterInvitationFailure.None => "tester-invite-unchanged",
        TesterInvitationFailure.InvalidEmail => "tester-invite-invalid-email",
        TesterInvitationFailure.NotPlatformAdmin => "tester-invite-not-admin",
        _ => "tester-invite-account-failed",
    };
    return Results.LocalRedirect($"/admin/users?testers=true&notice={notice}");
}).RequireAuthorization(PlatformAdministration.Policy);

app.MapPost("/admin/impersonation/stop", async (
    HttpContext context,
    IAntiforgery antiforgery,
    IPlatformAdminImpersonationAudit impersonationAudit,
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    CancellationToken cancellationToken) =>
{
    await antiforgery.ValidateRequestAsync(context);
    var administratorId = PlatformAdminImpersonation.ImpersonatorUserId(context.User);
    var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (administratorId is null || userId is null)
    {
        return Results.LocalRedirect("~/");
    }

    var stillAdministrator = await impersonationAudit.RecordStopAsync(administratorId, userId, "stopped", cancellationToken);
    var administrator = stillAdministrator ? await userManager.FindByIdAsync(administratorId) : null;
    if (administrator is null)
    {
        await signInManager.SignOutAsync();
        return Results.LocalRedirect("/Account/Login");
    }

    await signInManager.SignInAsync(administrator, isPersistent: false);
    return Results.LocalRedirect("/admin/users");
}).RequireAuthorization();

app.MapGet("/property-media/{mediaId:guid}", async (
    Guid mediaId,
    HttpContext context,
    IPropertyMediaService mediaService,
    CancellationToken cancellationToken) =>
{
    var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var media = await mediaService.OpenReadAsync(userId, mediaId, cancellationToken);
    return media is null
        ? Results.NotFound()
        : Results.Stream(media.Content, media.MimeType, enableRangeProcessing: true);
}).RequireAuthorization();

app.MapGet("/property-media/{mediaId:guid}/original", async (
    Guid mediaId,
    HttpContext context,
    IPropertyPhotoFixService photoFixService,
    CancellationToken cancellationToken) =>
{
    var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var media = await photoFixService.OpenOriginalReadAsync(userId, mediaId, cancellationToken);
    return media is null
        ? Results.NotFound()
        : Results.Stream(media.Content, media.MimeType, enableRangeProcessing: true);
}).RequireAuthorization();

app.MapGet("/photo-fixes/{fixId:guid}/candidate", async (
    Guid fixId,
    HttpContext context,
    IPropertyPhotoFixService photoFixService,
    CancellationToken cancellationToken) =>
{
    var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var media = await photoFixService.OpenCandidateReadAsync(userId, fixId, cancellationToken);
    return media is null
        ? Results.NotFound()
        : Results.Stream(media.Content, media.MimeType, enableRangeProcessing: true);
}).RequireAuthorization();

app.MapGet("/walkthrough-transitions/{transitionId:guid}", async (
    Guid transitionId,
    HttpContext context,
    IWalkthroughTransitionService transitionService,
    CancellationToken cancellationToken) =>
{
    var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var clip = await transitionService.OpenReadAsync(userId, transitionId, cancellationToken);
    return clip is null
        ? Results.NotFound()
        : Results.Stream(clip.Content, clip.ContentType, enableRangeProcessing: true);
}).RequireAuthorization();

app.MapGet("/aerial-intros/{clipId:guid}", async (
    Guid clipId,
    HttpContext context,
    IAerialIntroService aerialIntroService,
    CancellationToken cancellationToken) =>
{
    var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var clip = await aerialIntroService.OpenReadAsync(userId, clipId, cancellationToken);
    return clip is null
        ? Results.NotFound()
        : Results.Stream(clip.Content, clip.ContentType, enableRangeProcessing: true);
}).RequireAuthorization();

app.MapGet("/property-videos/{videoId:guid}", async (
    Guid videoId,
    bool? enhanced,
    bool? download,
    HttpContext context,
    IPropertyVideoService videoService,
    CancellationToken cancellationToken) =>
{
    var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var video = await videoService.OpenReadAsync(userId, videoId, enhanced == true, cancellationToken);
    return video is null
        ? Results.NotFound()
        : Results.Stream(
            video.Content,
            video.MimeType,
            fileDownloadName: download == true ? video.Filename : null,
            enableRangeProcessing: true);
}).RequireAuthorization();

app.MapGet("/property-narration-scripts/{scriptId:guid}", async (
    Guid scriptId,
    HttpContext context,
    IPropertyNarrationScriptService scriptService,
    CancellationToken cancellationToken) =>
{
    var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var script = await scriptService.OpenReadAsync(userId, scriptId, cancellationToken);
    return script is null
        ? Results.NotFound()
        : Results.Stream(script.Content, script.ContentType, script.Filename);
}).RequireAuthorization();

app.MapGet("/neighborhood-insights/{insightId:guid}/photo", async (
    Guid insightId,
    HttpContext context,
    INeighborhoodInsightService neighborhoodService,
    CancellationToken cancellationToken) =>
{
    var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var photo = await neighborhoodService.OpenPhotoAsync(userId, insightId, cancellationToken);
    return photo is null
        ? Results.NotFound()
        : Results.Stream(photo.Content, photo.ContentType);
}).RequireAuthorization();

app.MapGet("/neighborhood-insights/{insightId:guid}/video-photo", async (
    Guid insightId,
    HttpContext context,
    INeighborhoodInsightService neighborhoodService,
    CancellationToken cancellationToken) =>
{
    var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var photo = await neighborhoodService.OpenVideoPhotoAsync(userId, insightId, cancellationToken);
    return photo is null
        ? Results.NotFound()
        : Results.Stream(photo.Content, photo.ContentType, enableRangeProcessing: true);
}).RequireAuthorization();

app.MapGet("/campaigns/{jobId:guid}/deliverables/{kind}", async (
    Guid jobId,
    CampaignOutputKind kind,
    bool? download,
    HttpContext context,
    ICampaignGenerationService campaignService,
    CancellationToken cancellationToken) =>
{
    var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var deliverable = await campaignService.OpenDeliverableAsync(userId, jobId, kind, cancellationToken);
    if (deliverable is null)
    {
        return Results.NotFound();
    }

    return download is true
        ? Results.Stream(
            deliverable.Content,
            deliverable.ContentType,
            deliverable.FileName,
            enableRangeProcessing: true)
        : Results.Stream(
            deliverable.Content,
            deliverable.ContentType,
            enableRangeProcessing: true);
}).RequireAuthorization();

app.MapGet("/campaigns/{jobId:guid}/still-posts/{kind}", async (
    Guid jobId,
    CampaignStillPostKind kind,
    bool? download,
    HttpContext context,
    ICampaignGenerationService campaignService,
    CancellationToken cancellationToken) =>
{
    var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var post = await campaignService.OpenStillPostAsync(userId, jobId, kind, cancellationToken);
    if (post is null)
    {
        return Results.NotFound();
    }

    return download is true
        ? Results.Stream(post.Content, post.ContentType, post.FileName)
        : Results.Stream(post.Content, post.ContentType);
}).RequireAuthorization();

// A 15-second sample of a library soundtrack for the Campaign tab's music preview.
app.MapGet("/campaigns/soundtracks/{soundtrack}", async (
    CampaignSoundtrack soundtrack,
    ICampaignSoundtrackService soundtrackService,
    CancellationToken cancellationToken) =>
{
    if (soundtrack is not (CampaignSoundtrack.HeritageWarm or CampaignSoundtrack.ModernAmbient
        or CampaignSoundtrack.UpbeatLifestyle))
    {
        return Results.NotFound();
    }

    var directory = Path.Combine(Path.GetTempPath(), "listing-studio-soundtrack-previews", Guid.NewGuid().ToString("N"));
    try
    {
        var asset = await soundtrackService.MaterializeAsync(
            CampaignCreativeDirector.SoundtrackAssetId(soundtrack),
            15_000,
            directory,
            cancellationToken);
        return asset is null
            ? Results.NotFound()
            : Results.File(await File.ReadAllBytesAsync(asset.FilePath, cancellationToken), "audio/wav");
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}).RequireAuthorization();

app.MapGet("/agent-portraits/{portraitId:guid}", async (
    Guid portraitId,
    HttpContext context,
    IAgentPortraitService portraitService,
    CancellationToken cancellationToken) =>
{
    var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var portrait = await portraitService.OpenReadAsync(userId, portraitId, cancellationToken);
    return portrait is null
        ? Results.NotFound()
        : Results.Stream(portrait.Content, portrait.ContentType, enableRangeProcessing: true);
}).RequireAuthorization();

app.MapGet("/agent-portraits/{portraitId:guid}/avatar-preview", async (
    Guid portraitId,
    HttpContext context,
    IAgentAvatarPreviewService previewService,
    CancellationToken cancellationToken) =>
{
    var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var preview = await previewService.OpenReadAsync(userId, portraitId, cancellationToken);
    return preview is null
        ? Results.NotFound()
        : Results.Stream(preview.Content, preview.ContentType, enableRangeProcessing: true);
}).RequireAuthorization();

app.MapGet("/public/listings/{publicSlug}/cover", async (
    string publicSlug,
    ILeadGenerationService leadService,
    CancellationToken cancellationToken) =>
{
    var asset = await leadService.OpenCoverAsync(publicSlug, cancellationToken);
    return asset is null
        ? Results.NotFound()
        : Results.Stream(asset.Content, asset.ContentType, enableRangeProcessing: true);
}).AllowAnonymous();

app.MapGet("/public/listings/{publicSlug}/video", async (
    string publicSlug,
    ILeadGenerationService leadService,
    CancellationToken cancellationToken) =>
{
    var asset = await leadService.OpenVideoAsync(publicSlug, cancellationToken);
    return asset is null
        ? Results.NotFound()
        : Results.Stream(asset.Content, asset.ContentType, enableRangeProcessing: true);
}).AllowAnonymous();

app.MapGet("/public/listings/{publicSlug}/qr", async (
    string publicSlug,
    string? source,
    HttpContext context,
    ILeadGenerationService leadService,
    CancellationToken cancellationToken) =>
{
    if (await leadService.GetPublicListingAsync(publicSlug, cancellationToken) is null)
    {
        return Results.NotFound();
    }

    var normalizedSource = string.IsNullOrWhiteSpace(source) ? "qr" : source.Trim().ToLowerInvariant();
    if (normalizedSource.Length > 32 || normalizedSource.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
    {
        return Results.BadRequest();
    }

    var destination = $"{context.Request.Scheme}://{context.Request.Host}/listing/{Uri.EscapeDataString(publicSlug)}"
        + $"?utm_source={Uri.EscapeDataString(normalizedSource)}&utm_medium=offline&utm_campaign=property";
    using var qrData = QRCodeGenerator.GenerateQrCode(destination, QRCodeGenerator.ECCLevel.Q);
    var svg = new SvgQRCode(qrData).GetGraphic(4);
    return Results.Content(svg, "image/svg+xml", Encoding.UTF8);
}).AllowAnonymous();

app.MapGet("/leads/export.csv", async (
    HttpContext context,
    ILeadGenerationService leadService,
    CancellationToken cancellationToken) =>
{
    var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var content = await leadService.ExportCsvAsync(userId, cancellationToken);
    return Results.File(content, "text/csv; charset=utf-8", "listing-studio-leads.csv");
}).RequireAuthorization();

app.MapPost("/billing/checkout", async (
    HttpContext context,
    IAntiforgery antiforgery,
    IBillingService billingService,
    IOptions<StripeOptions> stripeOptions,
    CancellationToken cancellationToken) =>
{
    await antiforgery.ValidateRequestAsync(context);
    if (PlatformAdminImpersonation.IsImpersonating(context.User))
    {
        // Administrators can look at a customer's workspace but must not start or change their payments.
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var form = await context.Request.ReadFormAsync(cancellationToken);
    if (!Enum.TryParse<SubscriptionPlan>(form["plan"], ignoreCase: true, out var plan)
        || plan is not (SubscriptionPlan.Starter or SubscriptionPlan.Professional))
    {
        return Results.BadRequest("A valid paid plan is required.");
    }

    var baseUri = BillingBaseUri(context, stripeOptions.Value);
    var redirect = await billingService.CreateCheckoutSessionAsync(
        userId,
        plan,
        new Uri(baseUri, "/billing?checkout=success").AbsoluteUri,
        new Uri(baseUri, "/billing?checkout=cancelled").AbsoluteUri,
        cancellationToken);
    return Results.Redirect(redirect.Url);
}).RequireAuthorization();

app.MapPost("/billing/portal", async (
    HttpContext context,
    IAntiforgery antiforgery,
    IBillingService billingService,
    IOptions<StripeOptions> stripeOptions,
    CancellationToken cancellationToken) =>
{
    await antiforgery.ValidateRequestAsync(context);
    if (PlatformAdminImpersonation.IsImpersonating(context.User))
    {
        // Administrators can look at a customer's workspace but must not start or change their payments.
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var baseUri = BillingBaseUri(context, stripeOptions.Value);
    var redirect = await billingService.CreatePortalSessionAsync(
        userId,
        new Uri(baseUri, "/billing").AbsoluteUri,
        cancellationToken);
    return Results.Redirect(redirect.Url);
}).RequireAuthorization();

app.MapPost("/billing/stripe-webhook", async (
    HttpContext context,
    IBillingService billingService,
    CancellationToken cancellationToken) =>
{
    if (!context.Request.Headers.TryGetValue("Stripe-Signature", out var signature))
    {
        return Results.BadRequest();
    }

    using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
    var payload = await reader.ReadToEndAsync(cancellationToken);
    try
    {
        var outcome = await billingService.ProcessWebhookAsync(payload, signature.ToString(), cancellationToken);
        return Results.Ok(new { outcome });
    }
    catch (Exception exception) when (exception is InvalidOperationException or JsonException or ArgumentException)
    {
        return Results.BadRequest();
    }
}).DisableAntiforgery().WithMetadata(new RequestSizeLimitAttribute(1_048_576));

app.Run();

static Uri BillingBaseUri(HttpContext context, StripeOptions stripe)
{
    if (Uri.TryCreate(stripe.PublicBaseUrl, UriKind.Absolute, out var configured))
    {
        return new Uri(configured.AbsoluteUri.TrimEnd('/') + '/', UriKind.Absolute);
    }

    if (!stripe.Enabled)
    {
        return new Uri($"{context.Request.Scheme}://{context.Request.Host}");
    }

    throw new InvalidOperationException("Stripe:PublicBaseUrl is required when billing is enabled.");
}

static string LocalReturnUrl(string? returnUrl)
{
    if (string.IsNullOrWhiteSpace(returnUrl)
        || !returnUrl.StartsWith('/')
        || returnUrl.StartsWith("//", StringComparison.Ordinal)
        || returnUrl.StartsWith("/\\", StringComparison.Ordinal))
    {
        return "/";
    }

    return returnUrl;
}

public partial class Program;
