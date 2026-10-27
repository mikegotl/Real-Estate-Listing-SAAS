using ListingStudio.Domain.Videos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ListingStudio.Infrastructure.Persistence.Configurations;

public sealed class AgentAvatarVideoConfiguration : IEntityTypeConfiguration<AgentAvatarVideo>
{
    public void Configure(EntityTypeBuilder<AgentAvatarVideo> builder)
    {
        builder.ToTable("AgentAvatarVideos");
        builder.HasKey(video => video.Id);
        builder.Property(video => video.SourceFingerprint).HasMaxLength(64).IsRequired();
        builder.Property(video => video.Provider).HasMaxLength(100).IsRequired();
        builder.Property(video => video.Model).HasMaxLength(200).IsRequired();
        builder.Property(video => video.GenerationVersion).HasMaxLength(100).IsRequired();
        builder.Property(video => video.ProviderRequestId).HasMaxLength(200);
        builder.Property(video => video.ProviderMetadataJson).HasColumnType("jsonb").IsRequired();
        builder.Property(video => video.EstimatedCostUsd).HasPrecision(12, 6);
        builder.Property(video => video.AssetPath).HasMaxLength(1_024).IsRequired();
        builder.Property(video => video.ContentType).HasMaxLength(100).IsRequired();
        builder.HasIndex(video => video.AssetPath).IsUnique();
        builder.HasIndex(video => new { video.OrganizationId, video.PropertyId, video.SourceFingerprint }).IsUnique();
        builder.HasIndex(video => new { video.OrganizationId, video.PropertyId, video.CreatedAtUtc });

        builder.HasOne(video => video.Organization)
            .WithMany()
            .HasForeignKey(video => video.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(video => video.Property)
            .WithMany()
            .HasForeignKey(video => new { video.PropertyId, video.OrganizationId })
            .HasPrincipalKey(property => new { property.Id, property.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(video => video.AgentPortrait)
            .WithMany()
            .HasForeignKey(video => new { video.AgentPortraitId, video.OrganizationId })
            .HasPrincipalKey(portrait => new { portrait.Id, portrait.OrganizationId })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(video => video.VideoNarration)
            .WithMany()
            .HasForeignKey(video => new
            {
                video.VideoNarrationId,
                video.PropertyId,
                video.OrganizationId,
            })
            .HasPrincipalKey(narration => new
            {
                narration.Id,
                narration.PropertyId,
                narration.OrganizationId,
            })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
