using System.Security.Claims;
using ListingStudio.AI.DependencyInjection;
using ListingStudio.Application.Properties;
using ListingStudio.Application.DependencyInjection;
using ListingStudio.Infrastructure.DependencyInjection;
using ListingStudio.Infrastructure.Identity;
using ListingStudio.Video.DependencyInjection;
using ListingStudio.Web.Components;
using ListingStudio.Web.Components.Account;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using MudBlazor.Services;

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

app.Run();

public partial class Program;
