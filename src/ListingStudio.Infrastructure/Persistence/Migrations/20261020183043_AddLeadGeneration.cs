using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLeadGeneration : Migration
    {
        private static readonly string[] PropertyPrincipalKey = ["Id", "OrganizationId"];
        private static readonly string[] CapturePagePrincipalKey = ["Id", "OrganizationId"];
        private static readonly string[] LeadTenantKey = ["LeadId", "OrganizationId"];
        private static readonly string[] TenantLeadCreatedIndex = ["OrganizationId", "LeadId", "CreatedAtUtc"];
        private static readonly string[] TenantPropertyIndex = ["OrganizationId", "PropertyId"];
        private static readonly string[] PropertyTenantIndex = ["PropertyId", "OrganizationId"];
        private static readonly string[] CaptureEmailCreatedIndex = ["LeadCapturePageId", "Email", "CreatedAtUtc"];
        private static readonly string[] CaptureTenantIndex = ["LeadCapturePageId", "OrganizationId"];
        private static readonly string[] TenantCreatedIndex = ["OrganizationId", "CreatedAtUtc"];
        private static readonly string[] TenantStatusFollowUpIndex = ["OrganizationId", "Status", "FollowUpAtUtc"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LeadCapturePages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    PublicSlug = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Headline = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    IsPublished = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeadCapturePages", x => x.Id);
                    table.UniqueConstraint("AK_LeadCapturePages_Id_OrganizationId", x => new { x.Id, x.OrganizationId });
                    table.ForeignKey(
                        name: "FK_LeadCapturePages_Properties_PropertyId_OrganizationId",
                        columns: x => new { x.PropertyId, x.OrganizationId },
                        principalTable: "Properties",
                        principalColumns: PropertyPrincipalKey,
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PropertyLeads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    LeadCapturePageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    Phone = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    PreferredContact = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Source = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Medium = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Campaign = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    EmailMarketingConsent = table.Column<bool>(type: "boolean", nullable: false),
                    SmsMarketingConsent = table.Column<bool>(type: "boolean", nullable: false),
                    ConsentVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FollowUpAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PropertyLeads", x => x.Id);
                    table.UniqueConstraint("AK_PropertyLeads_Id_OrganizationId", x => new { x.Id, x.OrganizationId });
                    table.ForeignKey(
                        name: "FK_PropertyLeads_LeadCapturePages_LeadCapturePageId_Organizati~",
                        columns: x => new { x.LeadCapturePageId, x.OrganizationId },
                        principalTable: "LeadCapturePages",
                        principalColumns: CapturePagePrincipalKey,
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PropertyLeads_Properties_PropertyId_OrganizationId",
                        columns: x => new { x.PropertyId, x.OrganizationId },
                        principalTable: "Properties",
                        principalColumns: PropertyPrincipalKey,
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LeadActivities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    LeadId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    ActorUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeadActivities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LeadActivities_PropertyLeads_LeadId_OrganizationId",
                        columns: x => new { x.LeadId, x.OrganizationId },
                        principalTable: "PropertyLeads",
                        principalColumns: PropertyPrincipalKey,
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LeadActivities_LeadId_OrganizationId",
                table: "LeadActivities",
                columns: LeadTenantKey);

            migrationBuilder.CreateIndex(
                name: "IX_LeadActivities_OrganizationId_LeadId_CreatedAtUtc",
                table: "LeadActivities",
                columns: TenantLeadCreatedIndex);

            migrationBuilder.CreateIndex(
                name: "IX_LeadCapturePages_OrganizationId_PropertyId",
                table: "LeadCapturePages",
                columns: TenantPropertyIndex,
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeadCapturePages_PropertyId_OrganizationId",
                table: "LeadCapturePages",
                columns: PropertyTenantIndex);

            migrationBuilder.CreateIndex(
                name: "IX_LeadCapturePages_PublicSlug",
                table: "LeadCapturePages",
                column: "PublicSlug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PropertyLeads_LeadCapturePageId_Email_CreatedAtUtc",
                table: "PropertyLeads",
                columns: CaptureEmailCreatedIndex);

            migrationBuilder.CreateIndex(
                name: "IX_PropertyLeads_LeadCapturePageId_OrganizationId",
                table: "PropertyLeads",
                columns: CaptureTenantIndex);

            migrationBuilder.CreateIndex(
                name: "IX_PropertyLeads_OrganizationId_CreatedAtUtc",
                table: "PropertyLeads",
                columns: TenantCreatedIndex);

            migrationBuilder.CreateIndex(
                name: "IX_PropertyLeads_OrganizationId_Status_FollowUpAtUtc",
                table: "PropertyLeads",
                columns: TenantStatusFollowUpIndex);

            migrationBuilder.CreateIndex(
                name: "IX_PropertyLeads_PropertyId_OrganizationId",
                table: "PropertyLeads",
                columns: PropertyTenantIndex);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LeadActivities");

            migrationBuilder.DropTable(
                name: "PropertyLeads");

            migrationBuilder.DropTable(
                name: "LeadCapturePages");
        }
    }
}
