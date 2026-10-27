using ListingStudio.Domain.Campaigns;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ListingStudio.Infrastructure.Persistence.Configurations;

public sealed class CampaignStillPostConfiguration : IEntityTypeConfiguration<CampaignStillPost>
{
    public void Configure(EntityTypeBuilder<CampaignStillPost> builder)
    {
        builder.ToTable("CampaignStillPosts");
        builder.HasKey(post => post.Id);
        builder.Property(post => post.Kind).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(post => post.Caption).HasMaxLength(4_000).IsRequired();
        builder.Property(post => post.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(post => post.AssetPath).HasMaxLength(1_024);
        builder.Property(post => post.ContentType).HasMaxLength(100);
        builder.HasIndex(post => post.AssetPath).IsUnique();
        builder.HasIndex(post => new
        {
            post.OrganizationId,
            post.CampaignGenerationJobId,
            post.Kind,
        }).IsUnique();

        builder.HasOne(post => post.CampaignGenerationJob)
            .WithMany(job => job.StillPosts)
            .HasForeignKey(post => new
            {
                post.CampaignGenerationJobId,
                post.PropertyId,
                post.OrganizationId,
            })
            .HasPrincipalKey(job => new { job.Id, job.PropertyId, job.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
