using ListingStudio.Domain.Properties;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ListingStudio.Infrastructure.Persistence.Configurations;

public sealed class PropertyVideoConfiguration : IEntityTypeConfiguration<PropertyVideo>
{
    public void Configure(EntityTypeBuilder<PropertyVideo> builder)
    {
        builder.ToTable("PropertyVideos");
        builder.HasKey(video => video.Id);
        builder.HasAlternateKey(video => new { video.Id, video.PropertyId, video.OrganizationId });
        builder.Property(video => video.OriginalBlobPath).HasMaxLength(1_024).IsRequired();
        builder.Property(video => video.OriginalFilename).HasMaxLength(255).IsRequired();
        builder.Property(video => video.OriginalMimeType).HasMaxLength(100).IsRequired();
        builder.Property(video => video.FrameRate).HasPrecision(8, 3);
        builder.Property(video => video.ProcessingStatus).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(video => video.ProcessingLastError).HasMaxLength(1_000);
        builder.Property(video => video.EnhancedBlobPath).HasMaxLength(1_024);
        builder.Property(video => video.EnhancedFrameRate).HasPrecision(8, 3);
        builder.Property(video => video.EnhancementVersion).HasMaxLength(100);
        builder.HasIndex(video => video.OriginalBlobPath).IsUnique();
        builder.HasIndex(video => video.EnhancedBlobPath).IsUnique();
        builder.HasIndex(video => new { video.OrganizationId, video.PropertyId, video.UploadedAtUtc });
        builder.HasIndex(video => new
        {
            video.ProcessingStatus,
            video.ProcessingNextAttemptAtUtc,
            video.ProcessingAttemptCount,
        }).HasDatabaseName("IX_PropertyVideos_ProcessingQueue");

        builder
            .HasOne(video => video.Property)
            .WithMany(property => property.Videos)
            .HasForeignKey(video => new { video.PropertyId, video.OrganizationId })
            .HasPrincipalKey(property => new { property.Id, property.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
