using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ListingStudio.Infrastructure.Persistence.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261030151000_AddTikTokTestSuites")]
public sealed class AddTikTokTestSuites : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(name: "TikTokTestSuites", columns: table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
            PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
            RequestedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
            Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
            CurrentCaseIndex = table.Column<int>(type: "integer", nullable: false),
            Error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
            ShortVoiceoverJobId = table.Column<Guid>(type: "uuid", nullable: true),
            MusicOnlyJobId = table.Column<Guid>(type: "uuid", nullable: true),
            FullVoiceoverJobId = table.Column<Guid>(type: "uuid", nullable: true),
            CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
        }, constraints: table => table.PrimaryKey("PK_TikTokTestSuites", x => x.Id));
        migrationBuilder.CreateIndex(name: "IX_TikTokTestSuites_Status_UpdatedAtUtc", table: "TikTokTestSuites", columns: new[] { "Status", "UpdatedAtUtc" });
        migrationBuilder.CreateIndex(name: "IX_TikTokTestSuites_OrganizationId", table: "TikTokTestSuites", column: "OrganizationId",
            unique: true, filter: "\"Status\" IN ('Queued', 'Running')");
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable(name: "TikTokTestSuites");
}
