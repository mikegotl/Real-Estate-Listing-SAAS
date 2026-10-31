using ListingStudio.Domain.Properties;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ListingStudio.Infrastructure.Persistence.Configurations;

public sealed class AerialIntroClipConfiguration : IEntityTypeConfiguration<AerialIntroClip>
{
    public void Configure(EntityTypeBuilder<AerialIntroClip> builder)
    {
        builder.ToTable("AerialIntroClips");
        builder.HasKey(clip => clip.Id);
        builder.Property(clip => clip.SourceBlobPath).HasMaxLength(1_024).IsRequired();
        builder.Property(clip => clip.GenerationVersion).HasMaxLength(100).IsRequired();
        builder.Property(clip => clip.PlaybackRate).HasPrecision(3, 2).HasDefaultValue(AerialIntroClip.BriskPlaybackRate);
        builder.Property(clip => clip.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(clip => clip.ProviderRequestId).HasMaxLength(200);
        builder.Property(clip => clip.LastError).HasMaxLength(AerialIntroClip.MaximumErrorLength);
        builder.Property(clip => clip.Provider).HasMaxLength(100);
        builder.Property(clip => clip.Model).HasMaxLength(200);
        builder.Property(clip => clip.AssetPath).HasMaxLength(1_024);
        builder.Property(clip => clip.ContentType).HasMaxLength(100);
        builder.Property(clip => clip.RequestedByUserId).HasMaxLength(450).IsRequired();
        builder.Property(clip => clip.ApprovedByUserId).HasMaxLength(450);
        builder.Ignore(clip => clip.IsApproved);
        builder.Ignore(clip => clip.CanRetry);
        builder.Ignore(clip => clip.IsInProgress);

        // One drone intro per property; asking again replaces it.
        builder.HasIndex(clip => new { clip.OrganizationId, clip.PropertyId }).IsUnique();
        builder.HasIndex(clip => new { clip.Status, clip.NextCheckAtUtc })
            .HasDatabaseName("IX_AerialIntroClips_Queue");

        builder.HasOne(clip => clip.Organization)
            .WithMany()
            .HasForeignKey(clip => clip.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(clip => clip.Property)
            .WithMany()
            .HasForeignKey(clip => new { clip.PropertyId, clip.OrganizationId })
            .HasPrincipalKey(property => new { property.Id, property.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<PropertyMedia>()
            .WithMany()
            .HasForeignKey(clip => new { clip.PropertyMediaId, clip.PropertyId, clip.OrganizationId })
            .HasPrincipalKey(media => new { media.Id, media.PropertyId, media.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
