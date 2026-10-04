using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // EF Core generates inline arrays for composite keys and indexes.

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGeneratedVideoClips : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_PropertyMedia_Id_PropertyId_OrganizationId",
                table: "PropertyMedia",
                columns: new[] { "Id", "PropertyId", "OrganizationId" });

            migrationBuilder.CreateTable(
                name: "GeneratedVideoClips",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyMediaId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MotionInstruction = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DurationMs = table.Column<int>(type: "integer", nullable: false),
                    AspectRatio = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: false),
                    Height = table.Column<int>(type: "integer", nullable: false),
                    Provider = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    GenerationVersion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ProviderRequestId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ProviderMetadataJson = table.Column<string>(type: "jsonb", nullable: false),
                    EstimatedCostUsd = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: true),
                    AssetPath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    FileSize = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GeneratedVideoClips", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GeneratedVideoClips_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GeneratedVideoClips_Properties_PropertyId_OrganizationId",
                        columns: x => new { x.PropertyId, x.OrganizationId },
                        principalTable: "Properties",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GeneratedVideoClips_PropertyMedia_PropertyMediaId_PropertyI~",
                        columns: x => new { x.PropertyMediaId, x.PropertyId, x.OrganizationId },
                        principalTable: "PropertyMedia",
                        principalColumns: new[] { "Id", "PropertyId", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedVideoClips_AssetPath",
                table: "GeneratedVideoClips",
                column: "AssetPath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedVideoClips_OrganizationId_PropertyId_CreatedAtUtc",
                table: "GeneratedVideoClips",
                columns: new[] { "OrganizationId", "PropertyId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedVideoClips_OrganizationId_PropertyId_SourceFingerp~",
                table: "GeneratedVideoClips",
                columns: new[] { "OrganizationId", "PropertyId", "SourceFingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedVideoClips_PropertyId_OrganizationId",
                table: "GeneratedVideoClips",
                columns: new[] { "PropertyId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedVideoClips_PropertyMediaId_PropertyId_Organization~",
                table: "GeneratedVideoClips",
                columns: new[] { "PropertyMediaId", "PropertyId", "OrganizationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GeneratedVideoClips");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_PropertyMedia_Id_PropertyId_OrganizationId",
                table: "PropertyMedia");
        }
    }
}
#pragma warning restore CA1861
