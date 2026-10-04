using ListingStudio.Domain.Videos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ListingStudio.Infrastructure.Persistence.Configurations;

public sealed class VideoNarrationConfiguration : IEntityTypeConfiguration<VideoNarration>
{
    public void Configure(EntityTypeBuilder<VideoNarration> builder)
    {
        builder.ToTable("VideoNarrations");
        builder.HasKey(narration => narration.Id);
        builder.HasAlternateKey(narration => new
        {
            narration.Id,
            narration.PropertyId,
            narration.OrganizationId,
        });
        builder.Property(narration => narration.GenerationVersion).HasMaxLength(300).IsRequired();
        builder.Property(narration => narration.SourceFingerprint).HasMaxLength(64).IsRequired();
        builder.Property(narration => narration.AssetPath).HasMaxLength(1_024).IsRequired();
        builder.Property(narration => narration.ContentType).HasMaxLength(100).IsRequired();
        builder.Property(narration => narration.TimingJson).HasColumnType("jsonb");
        builder.HasIndex(narration => narration.AssetPath).IsUnique();
        builder.HasIndex(narration => new
        {
            narration.OrganizationId,
            narration.VideoProductionPlanId,
            narration.Version,
        }).IsUnique();
        builder.HasIndex(narration => new
        {
            narration.OrganizationId,
            narration.VideoProductionPlanId,
            narration.SourceFingerprint,
        }).IsUnique();

        builder.HasOne(narration => narration.Organization)
            .WithMany()
            .HasForeignKey(narration => narration.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(narration => narration.Property)
            .WithMany()
            .HasForeignKey(narration => new { narration.PropertyId, narration.OrganizationId })
            .HasPrincipalKey(property => new { property.Id, property.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(narration => narration.VideoProductionPlan)
            .WithMany()
            .HasForeignKey(narration => new
            {
                narration.VideoProductionPlanId,
                narration.PropertyId,
                narration.OrganizationId,
            })
            .HasPrincipalKey(plan => new { plan.Id, plan.PropertyId, plan.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
