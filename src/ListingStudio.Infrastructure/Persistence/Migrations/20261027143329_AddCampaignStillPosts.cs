using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1861 // EF Core generates inline arrays for composite keys and indexes.

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCampaignStillPosts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CampaignStillPosts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignGenerationJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: false),
                    Height = table.Column<int>(type: "integer", nullable: false),
                    Caption = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AssetPath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    FileSize = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RenderedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CampaignStillPosts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CampaignStillPosts_CampaignGenerationJobs_CampaignGeneratio~",
                        columns: x => new { x.CampaignGenerationJobId, x.PropertyId, x.OrganizationId },
                        principalTable: "CampaignGenerationJobs",
                        principalColumns: new[] { "Id", "PropertyId", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignStillPosts_AssetPath",
                table: "CampaignStillPosts",
                column: "AssetPath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CampaignStillPosts_CampaignGenerationJobId_PropertyId_Organ~",
                table: "CampaignStillPosts",
                columns: new[] { "CampaignGenerationJobId", "PropertyId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignStillPosts_OrganizationId_CampaignGenerationJobId_K~",
                table: "CampaignStillPosts",
                columns: new[] { "OrganizationId", "CampaignGenerationJobId", "Kind" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CampaignStillPosts");
        }
    }
}
