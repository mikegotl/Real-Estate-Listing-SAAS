using System.Security.Claims;
using ListingStudio.AI.DependencyInjection;
using ListingStudio.Application.Properties;
using ListingStudio.Application.DependencyInjection;
using ListingStudio.Application.Campaigns;
using ListingStudio.Application.Billing;
using ListingStudio.Domain.Billing;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Infrastructure.DependencyInjection;
using ListingStudio.Infrastructure.Identity;
using ListingStudio.Video.DependencyInjection;
using ListingStudio.Web.Components;
using ListingStudio.Web.Components.Account;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using MudBlazor.Services;
using Microsoft.AspNetCore.Antiforgery;
using System.Text;
using System.Text.Json;
using ListingStudio.Infrastructure.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddMudServices();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();
builder.Services.AddSingleton<DevelopmentEmailStore>();
builder.Services.AddScoped<IEmailSender<ApplicationUser>, DevelopmentIdentityEmailSender>();
builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration)
    .AddAI(builder.Configuration)
    .AddVideo(builder.Configuration);

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.MapPost("/Account/Logout", async (
    SignInManager<ApplicationUser> signInManager,
    HttpContext context) =>
{
    await signInManager.SignOutAsync();
    return Results.LocalRedirect("~/");
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

app.MapGet("/campaigns/{jobId:guid}/deliverables/{kind}", async (
    Guid jobId,
    CampaignOutputKind kind,
    HttpContext context,
    ICampaignGenerationService campaignService,
    CancellationToken cancellationToken) =>
{
    var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var download = await campaignService.OpenDeliverableAsync(userId, jobId, kind, cancellationToken);
    return download is null
        ? Results.NotFound()
        : Results.Stream(
            download.Content,
            download.ContentType,
            download.FileName,
            enableRangeProcessing: true);
}).RequireAuthorization();

app.MapPost("/billing/checkout", async (
    HttpContext context,
    IAntiforgery antiforgery,
    IBillingService billingService,
    IOptions<StripeOptions> stripeOptions,
    CancellationToken cancellationToken) =>
{
    await antiforgery.ValidateRequestAsync(context);
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

public partial class Program;
