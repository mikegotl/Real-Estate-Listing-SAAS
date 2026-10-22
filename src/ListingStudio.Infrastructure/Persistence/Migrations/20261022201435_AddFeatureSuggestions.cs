using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFeatureSuggestions : Migration
    {
        private static readonly string[] CampaignSuggestionColumns = ["CampaignGenerationJobId", "OrganizationId"];
        private static readonly string[] OrganizationSubmittedColumns = ["OrganizationId", "SubmittedAtUtc"];
        private static readonly string[] PropertyOrganizationColumns = ["PropertyId", "OrganizationId"];
        private static readonly string[] IdOrganizationColumns = ["Id", "OrganizationId"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FeatureSuggestions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubmittedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    Comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    RequiredForActiveCampaign = table.Column<bool>(type: "boolean", nullable: false),
                    CampaignGenerationJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    SubmittedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeatureSuggestions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FeatureSuggestions_AspNetUsers_SubmittedByUserId",
                        column: x => x.SubmittedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FeatureSuggestions_CampaignGenerationJobs_CampaignGeneratio~",
                        columns: x => new { x.CampaignGenerationJobId, x.OrganizationId },
                        principalTable: "CampaignGenerationJobs",
                        principalColumns: IdOrganizationColumns,
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FeatureSuggestions_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FeatureSuggestions_Properties_PropertyId_OrganizationId",
                        columns: x => new { x.PropertyId, x.OrganizationId },
                        principalTable: "Properties",
                        principalColumns: IdOrganizationColumns,
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FeatureSuggestions_CampaignGenerationJobId_OrganizationId",
                table: "FeatureSuggestions",
                columns: CampaignSuggestionColumns);

            migrationBuilder.CreateIndex(
                name: "IX_FeatureSuggestions_OrganizationId_SubmittedAtUtc",
                table: "FeatureSuggestions",
                columns: OrganizationSubmittedColumns);

            migrationBuilder.CreateIndex(
                name: "IX_FeatureSuggestions_PropertyId_OrganizationId",
                table: "FeatureSuggestions",
                columns: PropertyOrganizationColumns);

            migrationBuilder.CreateIndex(
                name: "IX_FeatureSuggestions_SubmittedByUserId",
                table: "FeatureSuggestions",
                column: "SubmittedByUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FeatureSuggestions");
        }
    }
}
