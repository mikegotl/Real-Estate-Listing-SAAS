using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using ListingStudio.Infrastructure.Configuration;

namespace ListingStudio.Infrastructure.Persistence;

public sealed class ApplicationDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var connectionString = new PostgreSqlOptions
        {
            ConnectionString = Environment.GetEnvironmentVariable("PostgreSQL__ConnectionString") ?? string.Empty,
            Host = Environment.GetEnvironmentVariable("PostgreSQL__Host") ?? string.Empty,
            Port = int.TryParse(Environment.GetEnvironmentVariable("PostgreSQL__Port"), out var port) ? port : 5432,
            Database = Environment.GetEnvironmentVariable("PostgreSQL__Database") ?? "listingstudio",
            Username = Environment.GetEnvironmentVariable("PostgreSQL__Username") ?? string.Empty,
            Password = Environment.GetEnvironmentVariable("PostgreSQL__Password") ?? string.Empty,
        }.BuildConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Set PostgreSQL__ConnectionString before creating or applying migrations.");
        }

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new ApplicationDbContext(options);
    }
}
