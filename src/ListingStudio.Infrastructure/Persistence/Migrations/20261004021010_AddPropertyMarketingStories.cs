using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPropertyMarketingStories : Migration
    {
        private static readonly string[] PropertyTenantKeyColumns = ["Id", "OrganizationId"];
        private static readonly string[] StoryFingerprintIndexColumns =
            ["OrganizationId", "PropertyId", "SourceFingerprint"];
        private static readonly string[] StoryVersionIndexColumns = ["OrganizationId", "PropertyId", "Version"];
        private static readonly string[] StoryPropertyTenantIndexColumns = ["PropertyId", "OrganizationId"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PropertyStories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    GenerationVersion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SourceFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CampaignTitle = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    OpeningHook = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    PropertyNarrative = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    VoiceoverScript = table.Column<string>(type: "character varying(6000)", maxLength: 6000, nullable: false),
                    ClosingCta = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    SocialCaptionLong = table.Column<string>(type: "character varying(2200)", maxLength: 2200, nullable: false),
                    SocialCaptionShort = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Highlights = table.Column<string[]>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PropertyStories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PropertyStories_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PropertyStories_Properties_PropertyId_OrganizationId",
                        columns: x => new { x.PropertyId, x.OrganizationId },
                        principalTable: "Properties",
                        principalColumns: PropertyTenantKeyColumns,
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PropertyStories_OrganizationId_PropertyId_SourceFingerprint",
                table: "PropertyStories",
                columns: StoryFingerprintIndexColumns,
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PropertyStories_OrganizationId_PropertyId_Version",
                table: "PropertyStories",
                columns: StoryVersionIndexColumns,
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PropertyStories_PropertyId_OrganizationId",
                table: "PropertyStories",
                columns: StoryPropertyTenantIndexColumns);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PropertyStories");
        }
    }
}
