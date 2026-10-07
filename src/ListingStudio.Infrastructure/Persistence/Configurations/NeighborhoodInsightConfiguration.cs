using ListingStudio.Domain.Neighborhoods;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ListingStudio.Infrastructure.Persistence.Configurations;

public sealed class NeighborhoodInsightConfiguration : IEntityTypeConfiguration<NeighborhoodInsight>
{
    public void Configure(EntityTypeBuilder<NeighborhoodInsight> builder)
    {
        builder.ToTable("NeighborhoodInsights");
        builder.HasKey(insight => insight.Id);
        builder.Property(insight => insight.ProviderPlaceId).HasMaxLength(300).IsRequired();
        builder.Property(insight => insight.Category).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(insight => insight.Name).HasMaxLength(300).IsRequired();
        builder.Property(insight => insight.Address).HasMaxLength(500).IsRequired();
        builder.Property(insight => insight.DistanceMiles).HasPrecision(7, 2);
        builder.Property(insight => insight.SourceUrl).HasMaxLength(2_000).IsRequired();
        builder.Property(insight => insight.PhotoAttribution).HasMaxLength(500);
        builder.Property(insight => insight.PhotoAttributionUrl).HasMaxLength(2_000);
        builder.Property(insight => insight.PhotoSourceUrl).HasMaxLength(2_000);
        builder.Property(insight => insight.VideoPhotoBlobPath).HasMaxLength(1_000);
        builder.Property(insight => insight.VideoPhotoFilename).HasMaxLength(255);
        builder.Property(insight => insight.VideoPhotoMimeType).HasMaxLength(100);
        builder.Property(insight => insight.VideoPhotoCredit).HasMaxLength(300);
        builder.HasIndex(insight => new { insight.OrganizationId, insight.PropertyId, insight.ProviderPlaceId }).IsUnique();
        builder.HasIndex(insight => new { insight.OrganizationId, insight.PropertyId, insight.IsApproved });

        builder.HasOne(insight => insight.Property)
            .WithMany(property => property.NeighborhoodInsights)
            .HasForeignKey(insight => new { insight.PropertyId, insight.OrganizationId })
            .HasPrincipalKey(property => new { property.Id, property.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
