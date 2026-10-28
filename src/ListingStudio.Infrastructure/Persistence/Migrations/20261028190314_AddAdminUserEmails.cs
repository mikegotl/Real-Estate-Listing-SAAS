using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1861 // EF Core generates inline arrays for composite keys and indexes.

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminUserEmails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdminUserEmails",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    SentByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    RecipientUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    RecipientEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    QueuedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SentAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminUserEmails", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdminUserEmails_BatchId",
                table: "AdminUserEmails",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_AdminUserEmails_QueuedAtUtc",
                table: "AdminUserEmails",
                column: "QueuedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AdminUserEmails_RecipientUserId",
                table: "AdminUserEmails",
                column: "RecipientUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AdminUserEmails_SentAtUtc_QueuedAtUtc",
                table: "AdminUserEmails",
                columns: new[] { "SentAtUtc", "QueuedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdminUserEmails");
        }
    }
}
