using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1861 // EF Core generates inline arrays for composite keys and indexes.

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRoomNarration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NarrationSourceChosenAtUtc",
                table: "Properties",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "UseRoomNarration",
                table: "Properties",
                type: "boolean",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WalkthroughStopNarrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Stop = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalkthroughStopNarrations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WalkthroughStopNarrations_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WalkthroughStopNarrations_Properties_PropertyId_Organizatio~",
                        columns: x => new { x.PropertyId, x.OrganizationId },
                        principalTable: "Properties",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WalkthroughStopNarrations_OrganizationId_PropertyId_Stop",
                table: "WalkthroughStopNarrations",
                columns: new[] { "OrganizationId", "PropertyId", "Stop" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WalkthroughStopNarrations_PropertyId_OrganizationId",
                table: "WalkthroughStopNarrations",
                columns: new[] { "PropertyId", "OrganizationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WalkthroughStopNarrations");

            migrationBuilder.DropColumn(
                name: "NarrationSourceChosenAtUtc",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "UseRoomNarration",
                table: "Properties");
        }
    }
}
