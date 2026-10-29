using ListingStudio.Domain.Properties;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ListingStudio.Infrastructure.Persistence.Configurations;

public sealed class PropertyPhotoFixConfiguration : IEntityTypeConfiguration<PropertyPhotoFix>
{
    public void Configure(EntityTypeBuilder<PropertyPhotoFix> builder)
    {
        builder.ToTable("PropertyPhotoFixes");
        builder.HasKey(fix => fix.Id);
        builder.Property(fix => fix.Kind).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(fix => fix.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(fix => fix.SourceBlobPath).HasMaxLength(1_024).IsRequired();
        builder.Property(fix => fix.EditMask).HasMaxLength(PhotoEditMask.MaximumStoredLength);
        builder.Property(fix => fix.CandidateBlobPath).HasMaxLength(1_024);
        builder.Property(fix => fix.CandidateMimeType).HasMaxLength(100);
        builder.Property(fix => fix.LastError).HasMaxLength(PropertyPhotoFix.MaximumErrorLength);
        builder.Ignore(fix => fix.IsInProgress);
        builder.HasIndex(fix => new { fix.OrganizationId, fix.PropertyId, fix.PropertyMediaId, fix.RequestedAtUtc });
        builder.HasIndex(fix => new { fix.Status, fix.NextAttemptAtUtc })
            .HasDatabaseName("IX_PropertyPhotoFixes_Queue");

        builder.HasOne<PropertyMedia>()
            .WithMany()
            .HasForeignKey(fix => new { fix.PropertyMediaId, fix.PropertyId, fix.OrganizationId })
            .HasPrincipalKey(media => new { media.Id, media.PropertyId, media.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
