using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1861 // EF Core generates inline arrays for composite keys and indexes.

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCampaignVideoChat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "CampaignDeliverables",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateTable(
                name: "CampaignVideoChatMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignGenerationJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignDeliverableId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    Role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    EditsJson = table.Column<string>(type: "jsonb", nullable: true),
                    VideoVersion = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CampaignVideoChatMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CampaignVideoRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignGenerationJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignDeliverableId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChatMessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    BaseVersion = table.Column<int>(type: "integer", nullable: false),
                    ResultVersion = table.Column<int>(type: "integer", nullable: true),
                    EditsJson = table.Column<string>(type: "jsonb", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeaseExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    PreviousSpecificationJson = table.Column<string>(type: "jsonb", nullable: true),
                    PreviousVideoNarrationId = table.Column<Guid>(type: "uuid", nullable: true),
                    PreviousAssetPath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    PreviousContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    PreviousFileSize = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CampaignVideoRevisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CampaignVideoRevisions_CampaignDeliverables_CampaignDeliver~",
                        column: x => x.CampaignDeliverableId,
                        principalTable: "CampaignDeliverables",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CampaignVideoRevisions_CampaignVideoChatMessages_ChatMessag~",
                        column: x => x.ChatMessageId,
                        principalTable: "CampaignVideoChatMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignVideoChatMessages_CreatedAtUtc",
                table: "CampaignVideoChatMessages",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_CampaignVideoChatMessages_OrganizationId_CampaignDeliverabl~",
                table: "CampaignVideoChatMessages",
                columns: new[] { "OrganizationId", "CampaignDeliverableId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignVideoRevisions_CampaignDeliverableId",
                table: "CampaignVideoRevisions",
                column: "CampaignDeliverableId");

            migrationBuilder.CreateIndex(
                name: "IX_CampaignVideoRevisions_ChatMessageId",
                table: "CampaignVideoRevisions",
                column: "ChatMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_CampaignVideoRevisions_OrganizationId_CampaignDeliverableId~",
                table: "CampaignVideoRevisions",
                columns: new[] { "OrganizationId", "CampaignDeliverableId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignVideoRevisions_Status_NextAttemptAtUtc",
                table: "CampaignVideoRevisions",
                columns: new[] { "Status", "NextAttemptAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CampaignVideoRevisions");

            migrationBuilder.DropTable(
                name: "CampaignVideoChatMessages");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "CampaignDeliverables");
        }
    }
}
#pragma warning restore CA1861
