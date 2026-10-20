using ListingStudio.Domain.Properties;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ListingStudio.Infrastructure.Persistence.Configurations;

public sealed class PropertyListingSourceConfiguration : IEntityTypeConfiguration<PropertyListingSource>
{
    public void Configure(EntityTypeBuilder<PropertyListingSource> builder)
    {
        builder.ToTable("PropertyListingSources");
        builder.HasKey(source => source.Id);
        builder.Property(source => source.Provider).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(source => source.SourceUrl).HasMaxLength(2_000).IsRequired();
        builder.Property(source => source.ExternalListingId).HasMaxLength(100).IsRequired();
        builder.Property(source => source.AddressConfirmedByUserId).HasMaxLength(450).IsRequired();
        builder.HasIndex(source => new { source.OrganizationId, source.PropertyId, source.Provider }).IsUnique();
        builder.HasIndex(source => new { source.Provider, source.ExternalListingId });

        builder.HasOne(source => source.Property)
            .WithMany(property => property.ListingSources)
            .HasForeignKey(source => new { source.PropertyId, source.OrganizationId })
            .HasPrincipalKey(property => new { property.Id, property.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
