using ListingStudio.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace ListingStudio.IntegrationTests;

public sealed class PostgreSqlWebApplicationFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgreSql = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("listingstudio_tests")
        .WithUsername("listingstudio")
        .WithPassword("integration-test-password")
        .Build();

    public ListingStudioWebApplicationFactory Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await postgreSql.StartAsync();
        Factory = new ListingStudioWebApplicationFactory(postgreSql.GetConnectionString());

        await using var scope = Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await postgreSql.DisposeAsync();
    }
}

public sealed class ListingStudioWebApplicationFactory(string connectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PostgreSQL:ConnectionString"] = connectionString,
            });
        });
    }
}
