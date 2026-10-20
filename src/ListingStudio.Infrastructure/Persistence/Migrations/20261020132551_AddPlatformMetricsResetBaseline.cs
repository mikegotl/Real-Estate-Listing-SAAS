using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformMetricsResetBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlatformMetricsResets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ResetAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformMetricsResets", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlatformMetricsResets_ResetAtUtc",
                table: "PlatformMetricsResets",
                column: "ResetAtUtc");

            migrationBuilder.Sql(
                """
                CREATE FUNCTION reject_platform_metrics_reset_mutation()
                RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'Platform metric resets are append-only.';
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER platform_metrics_reset_append_only
                BEFORE UPDATE OR DELETE ON "PlatformMetricsResets"
                FOR EACH ROW EXECUTE FUNCTION reject_platform_metrics_reset_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS platform_metrics_reset_append_only ON "PlatformMetricsResets";
                DROP FUNCTION IF EXISTS reject_platform_metrics_reset_mutation();
                """);

            migrationBuilder.DropTable(
                name: "PlatformMetricsResets");
        }
    }
}
