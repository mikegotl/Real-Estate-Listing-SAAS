using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPropertyListingSources : Migration
    {
        private static readonly string[] PropertyPrincipalKey = ["Id", "OrganizationId"];
        private static readonly string[] TenantProviderIndex = ["OrganizationId", "PropertyId", "Provider"];
        private static readonly string[] PropertyTenantIndex = ["PropertyId", "OrganizationId"];
        private static readonly string[] ProviderListingIndex = ["Provider", "ExternalListingId"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PropertyListingSources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SourceUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    ExternalListingId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AddressConfirmedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    AddressConfirmedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PropertyListingSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PropertyListingSources_Properties_PropertyId_OrganizationId",
                        columns: x => new { x.PropertyId, x.OrganizationId },
                        principalTable: "Properties",
                        principalColumns: PropertyPrincipalKey,
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PropertyListingSources_OrganizationId_PropertyId_Provider",
                table: "PropertyListingSources",
                columns: TenantProviderIndex,
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PropertyListingSources_PropertyId_OrganizationId",
                table: "PropertyListingSources",
                columns: PropertyTenantIndex);

            migrationBuilder.CreateIndex(
                name: "IX_PropertyListingSources_Provider_ExternalListingId",
                table: "PropertyListingSources",
                columns: ProviderListingIndex);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PropertyListingSources");
        }
    }
}
