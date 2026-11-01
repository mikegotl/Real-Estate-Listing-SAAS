using System.Data;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace ListingStudio.IntegrationTests;

/// <summary>
/// Prompt 12: upgrading a database from the last migration before the vertical video library, with representative
/// legacy rows, and migrating an empty database. Both must reach the current model with no pending changes, keep every
/// legacy row, and back-fill past vertical videos into legacy libraries.
/// </summary>
public sealed class PreFeatureMigrationTests(PostgreSqlWebApplicationFixture fixture) : IClassFixture<PostgreSqlWebApplicationFixture>
{
    /// <summary>The last migration before AddVerticalVideoLibrary (Prompt 3).</summary>
    public const string PreFeatureMigration = "20261031045031_AddAerialIntroPlaybackRate";

    private static readonly string[] FeatureMigrations =
    [
        "20261031211103_AddVerticalVideoLibrary",
        "20261031224909_AddVerticalMediaRolesAndClaims",
        "20261031232444_AddVerticalLibraryGeneration",
        "20261101002451_AddVerticalCampaignGoal",
        "20261101012216_AddVerticalCreativeTests",
        "20261101015629_AddVerticalPerformanceMetrics",
        "20261101150821_AddCreativeTestCaptionStyle",
    ];

    [Fact]
    public async Task APreFeatureDatabaseUpgradesKeepsLegacyRowsAndBackfillsLegacyLibraries()
    {
        await using var db = await NewDatabaseAsync("prefeature");
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(PreFeatureMigration);
        Assert.Equal(FeatureMigrations, (await db.Database.GetPendingMigrationsAsync()).ToArray());

        // Representative legacy data: a workspace, a listing, a finished campaign with a vertical video and a
        // landscape tour, and a running campaign. Every other required column gets a valid placeholder.
        var organization = Guid.NewGuid();
        var property = Guid.NewGuid();
        var finished = Guid.NewGuid();
        var running = Guid.NewGuid();
        var vertical = Guid.NewGuid();
        var landscape = Guid.NewGuid();
        await using (var connection = new NpgsqlConnection(db.Database.GetConnectionString()))
        {
            await connection.OpenAsync();
            await ExecuteAsync(connection, "SET session_replication_role = replica");
            await InsertAsync(connection, db.Model, "Organizations", new() { ["Id"] = organization });
            await InsertAsync(connection, db.Model, "Properties", new() { ["Id"] = property, ["OrganizationId"] = organization });
            foreach (var (job, status) in new[] { (finished, "Completed"), (running, "Running") })
            {
                await InsertAsync(connection, db.Model, "CampaignGenerationJobs", new()
                {
                    ["Id"] = job, ["OrganizationId"] = organization, ["PropertyId"] = property, ["Status"] = status,
                    ["RequestedOutputs"] = "TikTok, YouTubeTour",
                });
            }

            foreach (var (deliverable, kind, aspect) in new[] { (vertical, "TikTok", "Vertical9By16"), (landscape, "YouTubeTour", "Landscape16By9") })
            {
                await InsertAsync(connection, db.Model, "CampaignDeliverables", new()
                {
                    ["Id"] = deliverable, ["OrganizationId"] = organization, ["PropertyId"] = property, ["CampaignGenerationJobId"] = finished,
                    ["Kind"] = kind, ["Status"] = "Rendered", ["AspectRatio"] = aspect, ["RequestedDuration"] = 30,
                });
            }

            await ExecuteAsync(connection, "SET session_replication_role = DEFAULT");
        }

        await migrator.MigrateAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());

        // Every legacy row is still there and reads through the current model.
        db.ChangeTracker.Clear();
        var jobs = await db.CampaignGenerationJobs.AsNoTracking().Where(job => job.OrganizationId == organization).ToListAsync();
        Assert.Equal(2, jobs.Count);
        Assert.All(jobs, job => Assert.Null(job.VerticalSelection));
        Assert.All(jobs, job => Assert.Null(job.VerticalHookVariant));
        var deliverables = await db.CampaignDeliverables.AsNoTracking().Where(item => item.CampaignGenerationJobId == finished).ToListAsync();
        Assert.Equal(2, deliverables.Count);
        Assert.All(deliverables, item => Assert.True(item.IsCampaignLevel));

        // The past vertical video became a one-video legacy library; the landscape tour and the running job did not.
        var library = await db.CampaignVideoLibraries.AsNoTracking().Include(item => item.Variants)
            .SingleAsync(item => item.OrganizationId == organization);
        Assert.Equal(finished, library.CampaignGenerationJobId);
        Assert.Equal(VerticalVideoSelection.LegacySingle, library.Selection);
        Assert.Equal(CampaignVideoLibraryStatus.Completed, library.Status);
        var variant = Assert.Single(library.Variants);
        Assert.Equal(vertical, variant.Id);
        Assert.Equal(vertical, deliverables.Single(item => item.Id == vertical).CampaignVideoVariantId);
        Assert.Null(deliverables.Single(item => item.Id == landscape).CampaignVideoVariantId);

        // No feature data appears on its own.
        Assert.Equal(0, await db.VerticalCreativeTestSuites.CountAsync());
        Assert.Equal(0, await db.CampaignDistributionRecords.CountAsync());
        Assert.Equal(0, await db.CampaignMetricSnapshots.CountAsync());
        Assert.Equal(0, await db.VerticalRuleEvaluationReports.CountAsync());
        Assert.Equal(0, await db.FeatureFlags.CountAsync());
    }

    [Fact]
    public async Task AnEmptyDatabaseMigratesToTheCurrentModel()
    {
        await using var db = await NewDatabaseAsync("empty");
        await db.Database.MigrateAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(0, await db.FeatureFlags.CountAsync());
    }

    private async Task<ApplicationDbContext> NewDatabaseAsync(string prefix)
    {
        var name = $"{prefix}_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(fixture.ConnectionString))
        {
            await admin.OpenAsync();
            await ExecuteAsync(admin, $"CREATE DATABASE \"{name}\"");
        }

        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name }.ConnectionString;
        return new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName))
            .Options);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Inserts one row into a table as it exists at the pre-feature migration. Columns not given get a placeholder
    /// that fits both the column (from information_schema) and the current model (enum names for enum columns).
    /// </summary>
    private static async Task InsertAsync(NpgsqlConnection connection, IModel model, string table, Dictionary<string, object> values)
    {
        var entity = model.GetEntityTypes().Single(type => type.GetTableName() == table && type.FindPrimaryKey() is not null && !type.IsOwned());
        var storeTable = StoreObjectIdentifier.Table(table, entity.GetSchema());
        var columns = new List<(string Name, string DataType, int? Length)>();
        await using (var command = new NpgsqlCommand(
            """
            SELECT column_name, data_type, character_maximum_length FROM information_schema.columns
            WHERE table_name = @table AND is_nullable = 'NO' AND column_default IS NULL
            """, connection))
        {
            command.Parameters.AddWithValue("table", table);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt32(2)));
            }
        }

        foreach (var (name, dataType, length) in columns.Where(column => !values.ContainsKey(column.Name)))
        {
            var property = entity.GetProperties().FirstOrDefault(candidate => candidate.GetColumnName(storeTable) == name);
            var clr = Nullable.GetUnderlyingType(property?.ClrType ?? typeof(object)) ?? property?.ClrType;
            values[name] = clr is { IsEnum: true } ? (dataType is "integer" or "smallint" ? 0 : Enum.GetNames(clr)[0]) : dataType switch
            {
                "uuid" => Guid.NewGuid(),
                "integer" or "smallint" => 0,
                "bigint" => 0L,
                "numeric" => 0m,
                "double precision" or "real" => 0d,
                "boolean" => false,
                "timestamp with time zone" => DateTimeOffset.UtcNow,
                "timestamp without time zone" => DateTime.UtcNow,
                "date" => DateOnly.FromDateTime(DateTime.UtcNow),
                "jsonb" or "json" => "{}",
                "bytea" => Array.Empty<byte>(),
                _ when name.EndsWith("Json", StringComparison.Ordinal) => "{}",
                _ => new string('x', Math.Min(length ?? 8, 8)),
            };
        }

        var names = values.Keys.ToArray();
        await using var insert = new NpgsqlCommand(
            $"INSERT INTO \"{table}\" ({string.Join(", ", names.Select(name => $"\"{name}\""))}) VALUES ({string.Join(", ", names.Select((_, index) => $"@p{index}"))})",
            connection);
        for (var index = 0; index < names.Length; index++)
        {
            var value = values[names[index]];
            var parameter = insert.Parameters.AddWithValue($"p{index}", value);
            if (value is string && columns.FirstOrDefault(column => column.Name == names[index]).DataType is "jsonb")
            {
                parameter.NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb;
            }
        }

        await insert.ExecuteNonQueryAsync();
    }
}
