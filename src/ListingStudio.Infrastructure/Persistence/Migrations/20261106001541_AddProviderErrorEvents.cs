using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderErrorEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProviderErrorEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Provider = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Action = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    StatusCode = table.Column<int>(type: "integer", nullable: true),
                    ResponseText = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ProviderRequestId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RequestBody = table.Column<string>(type: "character varying(40000)", maxLength: 40000, nullable: true),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: true),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: true),
                    CampaignGenerationJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    CampaignVideoVariantId = table.Column<Guid>(type: "uuid", nullable: true),
                    Stage = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Attempt = table.Column<int>(type: "integer", nullable: true),
                    WillRetry = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderErrorEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderErrorEvents_CampaignGenerationJobId",
                table: "ProviderErrorEvents",
                column: "CampaignGenerationJobId");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderErrorEvents_OccurredAtUtc",
                table: "ProviderErrorEvents",
                column: "OccurredAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProviderErrorEvents");
        }
    }
}
