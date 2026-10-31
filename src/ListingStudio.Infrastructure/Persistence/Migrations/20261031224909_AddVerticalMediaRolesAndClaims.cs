using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Additive: media role decisions and agent-confirmed claims for the vertical video library. Both tables are
    /// append-only audit trails and are read only when VerticalVideoLibraryV1 features use them.
    /// </summary>
    public partial class AddVerticalMediaRolesAndClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ListingCreativeClaims",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    PrimaryText = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SecondaryText = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ValidUntilUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsWithdrawn = table.Column<bool>(type: "boolean", nullable: false),
                    ConfirmedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    ConfirmedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ListingCreativeClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ListingCreativeClaims_Properties_PropertyId_OrganizationId",
                        columns: x => new { x.PropertyId, x.OrganizationId },
                        principalTable: "Properties",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PropertyMediaRoleAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyMediaId = table.Column<Guid>(type: "uuid", nullable: true),
                    PropertyVideoId = table.Column<Guid>(type: "uuid", nullable: true),
                    Role = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Decision = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SetByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    SetAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PropertyMediaRoleAssignments", x => x.Id);
                    table.CheckConstraint("CK_PropertyMediaRoleAssignments_OneTarget", "(\"PropertyMediaId\" IS NULL) <> (\"PropertyVideoId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_PropertyMediaRoleAssignments_Properties_PropertyId_Organiza~",
                        columns: x => new { x.PropertyId, x.OrganizationId },
                        principalTable: "Properties",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PropertyMediaRoleAssignments_PropertyMedia_PropertyMediaId",
                        column: x => x.PropertyMediaId,
                        principalTable: "PropertyMedia",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PropertyMediaRoleAssignments_PropertyVideos_PropertyVideoId",
                        column: x => x.PropertyVideoId,
                        principalTable: "PropertyVideos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ListingCreativeClaims_OrganizationId_PropertyId_Kind_Confir~",
                table: "ListingCreativeClaims",
                columns: new[] { "OrganizationId", "PropertyId", "Kind", "ConfirmedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ListingCreativeClaims_PropertyId_OrganizationId",
                table: "ListingCreativeClaims",
                columns: new[] { "PropertyId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_PropertyMediaRoleAssignments_OrganizationId_PropertyId_SetA~",
                table: "PropertyMediaRoleAssignments",
                columns: new[] { "OrganizationId", "PropertyId", "SetAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PropertyMediaRoleAssignments_PropertyId_OrganizationId",
                table: "PropertyMediaRoleAssignments",
                columns: new[] { "PropertyId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_PropertyMediaRoleAssignments_PropertyMediaId",
                table: "PropertyMediaRoleAssignments",
                column: "PropertyMediaId");

            migrationBuilder.CreateIndex(
                name: "IX_PropertyMediaRoleAssignments_PropertyVideoId",
                table: "PropertyMediaRoleAssignments",
                column: "PropertyVideoId");
        }

        /// <summary>Development only. Production rollback leaves these tables in place and turns the switch off.</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ListingCreativeClaims");

            migrationBuilder.DropTable(
                name: "PropertyMediaRoleAssignments");
        }
    }
}
#pragma warning restore CA1861
