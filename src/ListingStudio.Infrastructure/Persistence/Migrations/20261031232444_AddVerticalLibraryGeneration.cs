using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Additive: the campaign remembers whether it asked for the vertical video library (null for every legacy
    /// campaign), and each library video gets a deterministic idempotency key, a hook variant and its own voiceover
    /// translation. Existing rows keep null keys and the default hook variant; nothing is rewritten.
    /// </summary>
    public partial class AddVerticalLibraryGeneration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HookVariant",
                table: "CampaignVideoVariants",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "a");

            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                table: "CampaignVideoVariants",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NarrationLocalizationJson",
                table: "CampaignVideoVariants",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerticalSelection",
                table: "CampaignGenerationJobs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CampaignVideoVariants_CampaignVideoLibraryId_IdempotencyKey",
                table: "CampaignVideoVariants",
                columns: new[] { "CampaignVideoLibraryId", "IdempotencyKey" },
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");
        }

        /// <summary>Development only. Production rollback leaves these columns in place and turns the switch off.</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CampaignVideoVariants_CampaignVideoLibraryId_IdempotencyKey",
                table: "CampaignVideoVariants");

            migrationBuilder.DropColumn(
                name: "HookVariant",
                table: "CampaignVideoVariants");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "CampaignVideoVariants");

            migrationBuilder.DropColumn(
                name: "NarrationLocalizationJson",
                table: "CampaignVideoVariants");

            migrationBuilder.DropColumn(
                name: "VerticalSelection",
                table: "CampaignGenerationJobs");
        }
    }
}
#pragma warning restore CA1861
