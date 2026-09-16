using ListingStudio.AI.DependencyInjection;
using ListingStudio.Application.DependencyInjection;
using ListingStudio.Infrastructure.DependencyInjection;
using ListingStudio.Video.DependencyInjection;
using ListingStudio.Web.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
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
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

public partial class Program;
