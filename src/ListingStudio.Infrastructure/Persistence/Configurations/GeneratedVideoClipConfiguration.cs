using ListingStudio.Domain.Videos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ListingStudio.Infrastructure.Persistence.Configurations;

public sealed class GeneratedVideoClipConfiguration : IEntityTypeConfiguration<GeneratedVideoClip>
{
    public void Configure(EntityTypeBuilder<GeneratedVideoClip> builder)
    {
        builder.ToTable("GeneratedVideoClips");
        builder.HasKey(clip => clip.Id);
        builder.Property(clip => clip.SourceFingerprint).HasMaxLength(64).IsRequired();
        builder.Property(clip => clip.MotionInstruction).HasMaxLength(200).IsRequired();
        builder.Property(clip => clip.AspectRatio).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(clip => clip.Provider).HasMaxLength(100).IsRequired();
        builder.Property(clip => clip.Model).HasMaxLength(200).IsRequired();
        builder.Property(clip => clip.GenerationVersion).HasMaxLength(100).IsRequired();
        builder.Property(clip => clip.ProviderRequestId).HasMaxLength(200);
        builder.Property(clip => clip.ProviderMetadataJson).HasColumnType("jsonb").IsRequired();
        builder.Property(clip => clip.EstimatedCostUsd).HasPrecision(12, 6);
        builder.Property(clip => clip.AssetPath).HasMaxLength(1_024).IsRequired();
        builder.Property(clip => clip.ContentType).HasMaxLength(100).IsRequired();
        builder.HasIndex(clip => clip.AssetPath).IsUnique();
        builder.HasIndex(clip => new { clip.OrganizationId, clip.PropertyId, clip.SourceFingerprint }).IsUnique();
        builder.HasIndex(clip => new { clip.OrganizationId, clip.PropertyId, clip.CreatedAtUtc });

        builder.HasOne(clip => clip.Organization)
            .WithMany()
            .HasForeignKey(clip => clip.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(clip => clip.Property)
            .WithMany()
            .HasForeignKey(clip => new { clip.PropertyId, clip.OrganizationId })
            .HasPrincipalKey(property => new { property.Id, property.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(clip => clip.PropertyMedia)
            .WithMany()
            .HasForeignKey(clip => new { clip.PropertyMediaId, clip.PropertyId, clip.OrganizationId })
            .HasPrincipalKey(media => new { media.Id, media.PropertyId, media.OrganizationId })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
