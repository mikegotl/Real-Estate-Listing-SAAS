using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditActorReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActorReference",
                table: "PlatformAdminAuditEvents",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActorReference",
                table: "PlatformAdminAuditEvents");
        }
    }
}
