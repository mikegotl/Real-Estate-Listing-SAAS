using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EmailCommentsToPlatformAdmins : Migration
    {
        private static readonly string[] AdminEmailColumns = ["AdminEmailSentAtUtc", "SubmittedAtUtc"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "PropertyId",
                table: "FeatureSuggestions",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<int>(
                name: "AdminEmailAttemptCount",
                table: "FeatureSuggestions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AdminEmailNextAttemptAtUtc",
                table: "FeatureSuggestions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AdminEmailSentAtUtc",
                table: "FeatureSuggestions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PagePath",
                table: "FeatureSuggestions",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_FeatureSuggestions_AdminEmailSentAtUtc_SubmittedAtUtc",
                table: "FeatureSuggestions",
                columns: AdminEmailColumns);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FeatureSuggestions_AdminEmailSentAtUtc_SubmittedAtUtc",
                table: "FeatureSuggestions");

            migrationBuilder.DropColumn(
                name: "AdminEmailAttemptCount",
                table: "FeatureSuggestions");

            migrationBuilder.DropColumn(
                name: "AdminEmailNextAttemptAtUtc",
                table: "FeatureSuggestions");

            migrationBuilder.DropColumn(
                name: "AdminEmailSentAtUtc",
                table: "FeatureSuggestions");

            migrationBuilder.DropColumn(
                name: "PagePath",
                table: "FeatureSuggestions");

            migrationBuilder.AlterColumn<Guid>(
                name: "PropertyId",
                table: "FeatureSuggestions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
