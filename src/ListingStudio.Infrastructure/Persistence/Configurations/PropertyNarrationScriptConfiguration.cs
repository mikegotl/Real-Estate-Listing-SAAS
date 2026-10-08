using ListingStudio.Domain.Properties;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ListingStudio.Infrastructure.Persistence.Configurations;

public sealed class PropertyNarrationScriptConfiguration : IEntityTypeConfiguration<PropertyNarrationScript>
{
    public void Configure(EntityTypeBuilder<PropertyNarrationScript> builder)
    {
        builder.ToTable("PropertyNarrationScripts");
        builder.HasKey(script => script.Id);
        builder.HasAlternateKey(script => new { script.Id, script.PropertyId, script.OrganizationId });
        builder.Property(script => script.BlobPath).HasMaxLength(1_024).IsRequired();
        builder.Property(script => script.OriginalFilename).HasMaxLength(255).IsRequired();
        builder.Property(script => script.ContentType).HasMaxLength(100).IsRequired();
        builder.Property(script => script.ExtractedText).HasMaxLength(12_000).IsRequired();
        builder.Property(script => script.MarketingUseAcceptedByUserId).HasMaxLength(450);
        builder.HasIndex(script => script.BlobPath).IsUnique();
        builder.HasIndex(script => new { script.OrganizationId, script.PropertyId }).IsUnique();

        builder
            .HasOne(script => script.Property)
            .WithOne(property => property.NarrationScript)
            .HasForeignKey<PropertyNarrationScript>(script => new { script.PropertyId, script.OrganizationId })
            .HasPrincipalKey<ListingProperty>(property => new { property.Id, property.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
