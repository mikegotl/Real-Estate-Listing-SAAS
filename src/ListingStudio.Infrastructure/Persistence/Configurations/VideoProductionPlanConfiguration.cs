using ListingStudio.Domain.Videos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ListingStudio.Infrastructure.Persistence.Configurations;

public sealed class VideoProductionPlanConfiguration : IEntityTypeConfiguration<VideoProductionPlan>
{
    public void Configure(EntityTypeBuilder<VideoProductionPlan> builder)
    {
        builder.ToTable("VideoProductionPlans");
        builder.HasKey(plan => plan.Id);
        builder.Property(plan => plan.RequestedDuration).HasConversion<int>().IsRequired();
        builder.Property(plan => plan.AspectRatio).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(plan => plan.SchemaVersion).HasMaxLength(20).IsRequired();
        builder.Property(plan => plan.DirectorVersion).HasMaxLength(100).IsRequired();
        builder.Property(plan => plan.SourceFingerprint).HasMaxLength(64).IsRequired();
        builder.Property(plan => plan.SpecificationJson).HasColumnType("jsonb").IsRequired();

        builder.HasIndex(plan => new
        {
            plan.OrganizationId,
            plan.PropertyId,
            plan.RequestedDuration,
            plan.AspectRatio,
            plan.Version,
        }).IsUnique();
        builder.HasIndex(plan => new
        {
            plan.OrganizationId,
            plan.PropertyId,
            plan.SourceFingerprint,
        }).IsUnique();

        builder.HasOne(plan => plan.Organization)
            .WithMany()
            .HasForeignKey(plan => plan.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(plan => plan.Property)
            .WithMany()
            .HasForeignKey(plan => new { plan.PropertyId, plan.OrganizationId })
            .HasPrincipalKey(property => new { property.Id, property.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(plan => plan.PropertyStory)
            .WithMany()
            .HasForeignKey(plan => new { plan.PropertyStoryId, plan.PropertyId, plan.OrganizationId })
            .HasPrincipalKey(story => new { story.Id, story.PropertyId, story.OrganizationId })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
