using ListingStudio.Domain.Campaigns;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ListingStudio.Infrastructure.Persistence.Configurations;

public sealed class CampaignDeliverableConfiguration : IEntityTypeConfiguration<CampaignDeliverable>
{
    public void Configure(EntityTypeBuilder<CampaignDeliverable> builder)
    {
        builder.ToTable("CampaignDeliverables");
        builder.HasKey(deliverable => deliverable.Id);
        builder.Property(deliverable => deliverable.Kind).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(deliverable => deliverable.RequestedDuration).HasConversion<int>().IsRequired();
        builder.Property(deliverable => deliverable.AspectRatio).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(deliverable => deliverable.SpecificationJson).HasColumnType("jsonb").IsRequired();
        builder.Property(deliverable => deliverable.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(deliverable => deliverable.AssetPath).HasMaxLength(1_024);
        builder.Property(deliverable => deliverable.ContentType).HasMaxLength(100);
        builder.HasIndex(deliverable => deliverable.AssetPath).IsUnique();
        builder.HasIndex(deliverable => new
        {
            deliverable.OrganizationId,
            deliverable.CampaignGenerationJobId,
            deliverable.Kind,
        }).IsUnique();

        builder.HasOne(deliverable => deliverable.CampaignGenerationJob)
            .WithMany(job => job.Deliverables)
            .HasForeignKey(deliverable => new
            {
                deliverable.CampaignGenerationJobId,
                deliverable.PropertyId,
                deliverable.OrganizationId,
            })
            .HasPrincipalKey(job => new { job.Id, job.PropertyId, job.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
