using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ListingStudio.Infrastructure.Persistence.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261031010000_AddTikTokSessionArchive")]
public sealed class AddTikTokSessionArchive : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<DateTimeOffset>(name: "ArchivedAtUtc", table: "TikTokTestSuites",
            type: "timestamp with time zone", nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(name: "ArchivedAtUtc", table: "TikTokTestSuites");
}
