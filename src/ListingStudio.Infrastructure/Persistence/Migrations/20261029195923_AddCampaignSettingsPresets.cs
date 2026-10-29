using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // EF Core generates inline arrays for composite keys and indexes.

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCampaignSettingsPresets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CampaignSettingsPresets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    SettingsJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    SchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    IsShared = table.Column<bool>(type: "boolean", nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CampaignSettingsPresets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CampaignSettingsPresets_AspNetUsers_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CampaignSettingsPresets_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignSettingsPresets_OneDefaultPerOwner",
                table: "CampaignSettingsPresets",
                columns: new[] { "OrganizationId", "OwnerUserId" },
                unique: true,
                filter: "\"IsDefault\"");

            migrationBuilder.CreateIndex(
                name: "IX_CampaignSettingsPresets_OrganizationId_IsShared",
                table: "CampaignSettingsPresets",
                columns: new[] { "OrganizationId", "IsShared" });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignSettingsPresets_OrganizationId_OwnerUserId_Normaliz~",
                table: "CampaignSettingsPresets",
                columns: new[] { "OrganizationId", "OwnerUserId", "NormalizedName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CampaignSettingsPresets_OwnerUserId",
                table: "CampaignSettingsPresets",
                column: "OwnerUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CampaignSettingsPresets");
        }
    }
}
