using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformAdministrationObservability : Migration
    {
        private static readonly string[] AiUsageIndexColumns = ["OccurredAtUtc", "Provider", "Operation"];
        private static readonly string[] OperationalIndexColumns = ["OccurredAtUtc", "Kind"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastSuccessfulSignInAtUtc",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AiUsageEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Operation = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Succeeded = table.Column<bool>(type: "boolean", nullable: false),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false),
                    EstimatedCostUsd = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    FailureCategory = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiUsageEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OperationalEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationalEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlatformAdminAuditEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ActorType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TargetReference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformAdminAuditEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageEvents_OccurredAtUtc_Provider_Operation",
                table: "AiUsageEvents",
                columns: AiUsageIndexColumns);

            migrationBuilder.CreateIndex(
                name: "IX_OperationalEvents_OccurredAtUtc_Kind",
                table: "OperationalEvents",
                columns: OperationalIndexColumns);

            migrationBuilder.CreateIndex(
                name: "IX_PlatformAdminAuditEvents_OccurredAtUtc",
                table: "PlatformAdminAuditEvents",
                column: "OccurredAtUtc");

            migrationBuilder.Sql(
                """
                CREATE FUNCTION reject_platform_admin_audit_mutation()
                RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'Platform administrator audit events are append-only.';
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER platform_admin_audit_append_only
                BEFORE UPDATE OR DELETE ON "PlatformAdminAuditEvents"
                FOR EACH ROW EXECUTE FUNCTION reject_platform_admin_audit_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS platform_admin_audit_append_only ON "PlatformAdminAuditEvents";
                DROP FUNCTION IF EXISTS reject_platform_admin_audit_mutation();
                """);

            migrationBuilder.DropTable(
                name: "AiUsageEvents");

            migrationBuilder.DropTable(
                name: "OperationalEvents");

            migrationBuilder.DropTable(
                name: "PlatformAdminAuditEvents");

            migrationBuilder.DropColumn(
                name: "LastSuccessfulSignInAtUtc",
                table: "AspNetUsers");
        }
    }
}
