using ListingStudio.Domain.Campaigns;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ListingStudio.Infrastructure.Persistence.Configurations;

public sealed class CampaignVideoReviewConfiguration : IEntityTypeConfiguration<CampaignVideoReview>
{
    public void Configure(EntityTypeBuilder<CampaignVideoReview> builder)
    {
        builder.ToTable("CampaignVideoReviews");
        builder.HasKey(review => review.Id);
        builder.Property(review => review.Sequence).UseIdentityByDefaultColumn().ValueGeneratedOnAdd();
        builder.Property(review => review.Decision).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(review => review.ReviewerUserId).HasMaxLength(450).IsRequired();
        builder.Property(review => review.Feedback).HasMaxLength(1_000);
        builder.HasIndex(review => new { review.OrganizationId, review.CampaignDeliverableId, review.CreatedAtUtc })
            .HasDatabaseName("IX_CampaignVideoReviews_Organization_Deliverable_Created");
        // Review history survives video deletion. Every service query remains organization-scoped.
    }
}
