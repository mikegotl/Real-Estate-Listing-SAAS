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
        builder.HasAlternateKey(media => new { media.Id, media.PropertyId, media.OrganizationId });
        builder.Property(media => media.BlobPath).HasMaxLength(1_024).IsRequired();
        builder.Property(media => media.OriginalFilename).HasMaxLength(255).IsRequired();
        builder.Property(media => media.MimeType).HasMaxLength(100).IsRequired();
        builder.Property(media => media.AnalysisStatus).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(media => media.Category).HasConversion<string>().HasMaxLength(64);
        builder.Property(media => media.RoomType).HasMaxLength(100);
        builder.Property(media => media.AnalysisDescription).HasMaxLength(2_000);
        builder.Property(media => media.AnalysisLastError).HasMaxLength(1_000);
        builder.Ignore(media => media.PotentialProblems);
        builder.Property<string[]>("potentialProblems")
            .HasColumnName("PotentialProblems")
            .HasColumnType("text[]")
            .IsRequired();
        builder.HasIndex(media => media.BlobPath).IsUnique();
        builder.HasIndex(media => new { media.OrganizationId, media.PropertyId, media.DisplayOrder });
        builder.HasIndex(media => new
        {
            media.AnalysisStatus,
            media.AnalysisNextAttemptAtUtc,
            media.AnalysisAttemptCount,
        }).HasDatabaseName("IX_PropertyMedia_AnalysisQueue");

        builder
            .HasOne(media => media.Property)
            .WithMany(property => property.Media)
            .HasForeignKey(media => new { media.PropertyId, media.OrganizationId })
            .HasPrincipalKey(property => new { property.Id, property.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
