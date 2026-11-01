using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // EF Core generates inline arrays for composite keys and indexes.

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Additive: the admin Creative Format Testing tab's suites and cases (a separate queue from the render regression
    /// suite), and CampaignGenerationJobs.VerticalHookVariant ("b" only for hook A/B test campaigns; null everywhere else).
    /// Nothing existing is rewritten.
    /// </summary>
    public partial class AddVerticalCreativeTests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "VerticalHookVariant",
                table: "CampaignGenerationJobs",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "VerticalCreativeTestSuites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    Mode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Format = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    RuleVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VerticalCreativeTestSuites", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VerticalCreativeTestCases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SuiteId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Index = table.Column<int>(type: "integer", nullable: false),
                    CaseKey = table.Column<Guid>(type: "uuid", nullable: false),
                    Format = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    HookVariant = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Score = table.Column<int>(type: "integer", nullable: false),
                    ReasonsJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    RecipeVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ExpectedDurationSeconds = table.Column<int>(type: "integer", nullable: false),
                    CampaignGenerationJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    CampaignVideoVariantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CampaignDeliverableId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FinishedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VerticalCreativeTestCases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VerticalCreativeTestCases_VerticalCreativeTestSuites_SuiteId",
                        column: x => x.SuiteId,
                        principalTable: "VerticalCreativeTestSuites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VerticalCreativeTestCases_CaseKey",
                table: "VerticalCreativeTestCases",
                column: "CaseKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VerticalCreativeTestCases_OrganizationId_CampaignDeliverabl~",
                table: "VerticalCreativeTestCases",
                columns: new[] { "OrganizationId", "CampaignDeliverableId" });

            migrationBuilder.CreateIndex(
                name: "IX_VerticalCreativeTestCases_OrganizationId_CampaignGeneration~",
                table: "VerticalCreativeTestCases",
                columns: new[] { "OrganizationId", "CampaignGenerationJobId" });

            migrationBuilder.CreateIndex(
                name: "IX_VerticalCreativeTestCases_SuiteId_Index",
                table: "VerticalCreativeTestCases",
                columns: new[] { "SuiteId", "Index" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VerticalCreativeTestSuites_OrganizationId_Active",
                table: "VerticalCreativeTestSuites",
                column: "OrganizationId",
                unique: true,
                filter: "\"Status\" IN ('Queued', 'Running')");

            migrationBuilder.CreateIndex(
                name: "IX_VerticalCreativeTestSuites_OrganizationId_CreatedAtUtc",
                table: "VerticalCreativeTestSuites",
                columns: new[] { "OrganizationId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_VerticalCreativeTestSuites_Status_UpdatedAtUtc",
                table: "VerticalCreativeTestSuites",
                columns: new[] { "Status", "UpdatedAtUtc" });
        }

        /// <summary>Development only. In production, roll back by turning VerticalVideoLibraryV1 off.</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VerticalCreativeTestCases");

            migrationBuilder.DropTable(
                name: "VerticalCreativeTestSuites");

            migrationBuilder.DropColumn(
                name: "VerticalHookVariant",
                table: "CampaignGenerationJobs");
        }
    }
}
#pragma warning restore CA1861
