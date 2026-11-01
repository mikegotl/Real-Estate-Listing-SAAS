using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ListingStudio.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Additive: the caption look a creative test uses. Tests created before it used the original look (Pop), which is
    /// also the column default for rows written by an older app.
    /// </summary>
    public partial class AddCreativeTestCaptionStyle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CaptionStyle",
                table: "VerticalCreativeTestSuites",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Pop");
        }

        /// <summary>Development only.</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CaptionStyle",
                table: "VerticalCreativeTestSuites");
        }
    }
}
