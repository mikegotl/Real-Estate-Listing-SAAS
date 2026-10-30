using System;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ListingStudio.Infrastructure.Persistence.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261030150000_AddCampaignVideoReviews")]
public sealed class AddCampaignVideoReviews : Migration
{
    private static readonly string[] ReviewIndexColumns = ["OrganizationId", "CampaignDeliverableId", "CreatedAtUtc"];
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "CampaignVideoReviews",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Sequence = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                CampaignDeliverableId = table.Column<Guid>(type: "uuid", nullable: false),
                VideoVersion = table.Column<int>(type: "integer", nullable: false),
                Decision = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                ReviewerUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                Feedback = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_CampaignVideoReviews", x => x.Id));
        migrationBuilder.CreateIndex(
            name: "IX_CampaignVideoReviews_Organization_Deliverable_Created",
            table: "CampaignVideoReviews",
            columns: ReviewIndexColumns);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropTable(name: "CampaignVideoReviews");
}
