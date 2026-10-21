using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // EF Core generates inline arrays for composite keys and indexes.

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCampaignCreativeControls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AgentPortraitId",
                table: "CampaignGenerationJobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CaptionStyle",
                table: "CampaignGenerationJobs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Classic");

            migrationBuilder.AddColumn<string>(
                name: "CreativeElements",
                table: "CampaignGenerationJobs",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "OpeningTitle, PropertyFacts, NeighborhoodHighlights, WalkthroughMotion, ClosingCallToAction");

            migrationBuilder.AddColumn<string>(
                name: "Soundtrack",
                table: "CampaignGenerationJobs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.CreateTable(
                name: "AgentPortraits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    BlobPath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    OriginalFilename = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    MimeType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    FileSize = table.Column<long>(type: "bigint", nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: false),
                    Height = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentPortraits", x => x.Id);
                    table.UniqueConstraint("AK_AgentPortraits_Id_OrganizationId", x => new { x.Id, x.OrganizationId });
                    table.ForeignKey(
                        name: "FK_AgentPortraits_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentPortraits_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignGenerationJobs_AgentPortraitId_OrganizationId",
                table: "CampaignGenerationJobs",
                columns: new[] { "AgentPortraitId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentPortraits_BlobPath",
                table: "AgentPortraits",
                column: "BlobPath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentPortraits_OrganizationId_UserId_UpdatedAtUtc",
                table: "AgentPortraits",
                columns: new[] { "OrganizationId", "UserId", "UpdatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentPortraits_UserId",
                table: "AgentPortraits",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_CampaignGenerationJobs_AgentPortraits_AgentPortraitId_Organ~",
                table: "CampaignGenerationJobs",
                columns: new[] { "AgentPortraitId", "OrganizationId" },
                principalTable: "AgentPortraits",
                principalColumns: new[] { "Id", "OrganizationId" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CampaignGenerationJobs_AgentPortraits_AgentPortraitId_Organ~",
                table: "CampaignGenerationJobs");

            migrationBuilder.DropTable(
                name: "AgentPortraits");

            migrationBuilder.DropIndex(
                name: "IX_CampaignGenerationJobs_AgentPortraitId_OrganizationId",
                table: "CampaignGenerationJobs");

            migrationBuilder.DropColumn(
                name: "AgentPortraitId",
                table: "CampaignGenerationJobs");

            migrationBuilder.DropColumn(
                name: "CaptionStyle",
                table: "CampaignGenerationJobs");

            migrationBuilder.DropColumn(
                name: "CreativeElements",
                table: "CampaignGenerationJobs");

            migrationBuilder.DropColumn(
                name: "Soundtrack",
                table: "CampaignGenerationJobs");
        }
    }
}
#pragma warning restore CA1861
