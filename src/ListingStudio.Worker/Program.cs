using ListingStudio.AI.DependencyInjection;
using ListingStudio.Application.DependencyInjection;
using ListingStudio.Infrastructure.DependencyInjection;
using ListingStudio.Video.DependencyInjection;
using ListingStudio.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration)
    .AddAI(builder.Configuration)
    .AddVideo(builder.Configuration);
builder.Services.AddHostedService<Worker>();

await builder.Build().RunAsync();
