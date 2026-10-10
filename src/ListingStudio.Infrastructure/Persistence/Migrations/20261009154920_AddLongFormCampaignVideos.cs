using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLongFormCampaignVideos : Migration
    {
        private static readonly string[] NarrationForeignKeyColumns =
            ["VideoNarrationId", "PropertyId", "OrganizationId"];
        private static readonly string[] NarrationPrincipalColumns =
            ["Id", "PropertyId", "OrganizationId"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "VideoNarrationId",
                table: "CampaignDeliverables",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "CampaignDeliverables" AS deliverable
                SET "VideoNarrationId" = job."VideoNarrationId"
                FROM "CampaignGenerationJobs" AS job
                WHERE deliverable."CampaignGenerationJobId" = job."Id"
                  AND job."VideoNarrationId" IS NOT NULL;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "VideoNarrationId",
                table: "CampaignDeliverables",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CampaignDeliverables_VideoNarrationId_PropertyId_Organizati~",
                table: "CampaignDeliverables",
                columns: NarrationForeignKeyColumns);

            migrationBuilder.AddForeignKey(
                name: "FK_CampaignDeliverables_VideoNarrations_VideoNarrationId_Prope~",
                table: "CampaignDeliverables",
                columns: NarrationForeignKeyColumns,
                principalTable: "VideoNarrations",
                principalColumns: NarrationPrincipalColumns,
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CampaignDeliverables_VideoNarrations_VideoNarrationId_Prope~",
                table: "CampaignDeliverables");

            migrationBuilder.DropIndex(
                name: "IX_CampaignDeliverables_VideoNarrationId_PropertyId_Organizati~",
                table: "CampaignDeliverables");

            migrationBuilder.DropColumn(
                name: "VideoNarrationId",
                table: "CampaignDeliverables");
        }
    }
}
