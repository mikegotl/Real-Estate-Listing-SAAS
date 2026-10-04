using ListingStudio.Domain.Properties;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ListingStudio.Infrastructure.Persistence.Configurations;

public sealed class PropertyConfiguration : IEntityTypeConfiguration<ListingProperty>
{
    public void Configure(EntityTypeBuilder<ListingProperty> builder)
    {
        builder.ToTable("Properties");
        builder.HasKey(property => property.Id);
        builder.HasAlternateKey(property => new { property.Id, property.OrganizationId });
        builder.Ignore(property => property.IsArchived);

        builder.Property(property => property.Address1).HasMaxLength(200).IsRequired();
        builder.Property(property => property.Address2).HasMaxLength(200);
        builder.Property(property => property.City).HasMaxLength(100).IsRequired();
        builder.Property(property => property.State).HasMaxLength(100).IsRequired();
        builder.Property(property => property.ZipCode).HasMaxLength(20).IsRequired();
        builder.Property(property => property.ListingPrice).HasPrecision(18, 2);
        builder.Property(property => property.Bathrooms).HasPrecision(4, 1);
        builder.Property(property => property.LotSize).HasPrecision(12, 2);
        builder.Property(property => property.PropertyType).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(property => property.Description).HasMaxLength(4_000);
        builder.Property(property => property.ListingStatus).HasConversion<string>().HasMaxLength(32).IsRequired();

        builder.HasIndex(property => new { property.OrganizationId, property.ArchivedAtUtc });
        builder.HasIndex(property => new { property.OrganizationId, property.ListingStatus });

        builder
            .HasOne(property => property.Organization)
            .WithMany(organization => organization.Properties)
            .HasForeignKey(property => property.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(property => property.Media).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
