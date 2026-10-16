using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWalkthroughStopDirections : Migration
    {
        private static readonly string[] PropertyKeyColumns =
            ["Id", "OrganizationId"];

        private static readonly string[] StopColumns =
            ["OrganizationId", "PropertyId", "Stop"];

        private static readonly string[] PropertyColumns =
            ["PropertyId", "OrganizationId"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WalkthroughStopDirections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Stop = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Direction = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalkthroughStopDirections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WalkthroughStopDirections_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WalkthroughStopDirections_Properties_PropertyId_Organizatio~",
                        columns: x => new { x.PropertyId, x.OrganizationId },
                        principalTable: "Properties",
                        principalColumns: PropertyKeyColumns,
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WalkthroughStopDirections_OrganizationId_PropertyId_Stop",
                table: "WalkthroughStopDirections",
                columns: StopColumns,
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WalkthroughStopDirections_PropertyId_OrganizationId",
                table: "WalkthroughStopDirections",
                columns: PropertyColumns);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WalkthroughStopDirections");
        }
    }
}
