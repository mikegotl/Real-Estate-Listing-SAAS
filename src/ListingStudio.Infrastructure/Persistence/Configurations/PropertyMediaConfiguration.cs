using ListingStudio.Domain.Properties;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ListingStudio.Infrastructure.Persistence.Configurations;

public sealed class PropertyMediaConfiguration : IEntityTypeConfiguration<PropertyMedia>
{
    public void Configure(EntityTypeBuilder<PropertyMedia> builder)
    {
        builder.ToTable("PropertyMedia");
        builder.HasKey(media => media.Id);
        builder.Property(media => media.BlobPath).HasMaxLength(1_024).IsRequired();
        builder.Property(media => media.OriginalFilename).HasMaxLength(255).IsRequired();
        builder.Property(media => media.MimeType).HasMaxLength(100).IsRequired();
        builder.Property(media => media.AnalysisStatus).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.HasIndex(media => media.BlobPath).IsUnique();
        builder.HasIndex(media => new { media.OrganizationId, media.PropertyId, media.DisplayOrder });

        builder
            .HasOne(media => media.Property)
            .WithMany(property => property.Media)
            .HasForeignKey(media => new { media.PropertyId, media.OrganizationId })
            .HasPrincipalKey(property => new { property.Id, property.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
