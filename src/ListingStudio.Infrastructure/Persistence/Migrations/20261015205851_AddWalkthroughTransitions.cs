using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWalkthroughTransitions : Migration
    {
        private static readonly string[] PropertyKeyColumns =
            ["Id", "OrganizationId"];

        private static readonly string[] MediaKeyColumns =
            ["Id", "PropertyId", "OrganizationId"];

        private static readonly string[] FromMediaColumns =
            ["FromPropertyMediaId", "PropertyId", "OrganizationId"];

        private static readonly string[] PairVersionColumns =
            ["OrganizationId", "PropertyId", "FromPropertyMediaId", "ToPropertyMediaId", "GenerationVersion"];

        private static readonly string[] PropertyColumns =
            ["PropertyId", "OrganizationId"];

        private static readonly string[] QueueColumns =
            ["Status", "NextCheckAtUtc"];

        private static readonly string[] ToMediaColumns =
            ["ToPropertyMediaId", "PropertyId", "OrganizationId"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WalkthroughTransitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromPropertyMediaId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToPropertyMediaId = table.Column<Guid>(type: "uuid", nullable: false),
                    GenerationVersion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    ProviderRequestId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    LastAttemptedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SubmittedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NextCheckAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Provider = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AssetPath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    FileSize = table.Column<long>(type: "bigint", nullable: true),
                    DurationMs = table.Column<int>(type: "integer", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalkthroughTransitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WalkthroughTransitions_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WalkthroughTransitions_Properties_PropertyId_OrganizationId",
                        columns: x => new { x.PropertyId, x.OrganizationId },
                        principalTable: "Properties",
                        principalColumns: PropertyKeyColumns,
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WalkthroughTransitions_PropertyMedia_FromPropertyMediaId_Pr~",
                        columns: x => new { x.FromPropertyMediaId, x.PropertyId, x.OrganizationId },
                        principalTable: "PropertyMedia",
                        principalColumns: MediaKeyColumns,
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WalkthroughTransitions_PropertyMedia_ToPropertyMediaId_Prop~",
                        columns: x => new { x.ToPropertyMediaId, x.PropertyId, x.OrganizationId },
                        principalTable: "PropertyMedia",
                        principalColumns: MediaKeyColumns,
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WalkthroughTransitions_FromPropertyMediaId_PropertyId_Organ~",
                table: "WalkthroughTransitions",
                columns: FromMediaColumns);

            migrationBuilder.CreateIndex(
                name: "IX_WalkthroughTransitions_OrganizationId_PropertyId_FromProper~",
                table: "WalkthroughTransitions",
                columns: PairVersionColumns,
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WalkthroughTransitions_PropertyId_OrganizationId",
                table: "WalkthroughTransitions",
                columns: PropertyColumns);

            migrationBuilder.CreateIndex(
                name: "IX_WalkthroughTransitions_Queue",
                table: "WalkthroughTransitions",
                columns: QueueColumns);

            migrationBuilder.CreateIndex(
                name: "IX_WalkthroughTransitions_ToPropertyMediaId_PropertyId_Organiz~",
                table: "WalkthroughTransitions",
                columns: ToMediaColumns);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WalkthroughTransitions");
        }
    }
}
