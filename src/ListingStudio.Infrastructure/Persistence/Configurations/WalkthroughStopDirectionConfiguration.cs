using ListingStudio.Domain.Properties;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ListingStudio.Infrastructure.Persistence.Configurations;

public sealed class WalkthroughStopDirectionConfiguration : IEntityTypeConfiguration<WalkthroughStopDirection>
{
    public void Configure(EntityTypeBuilder<WalkthroughStopDirection> builder)
    {
        builder.ToTable("WalkthroughStopDirections");
        builder.HasKey(direction => direction.Id);
        builder.Property(direction => direction.Stop).HasMaxLength(PropertyMedia.MaximumWalkthroughStopLength).IsRequired();
        builder.Property(direction => direction.Direction).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.HasIndex(direction => new { direction.OrganizationId, direction.PropertyId, direction.Stop }).IsUnique();

        builder.HasOne(direction => direction.Organization)
            .WithMany()
            .HasForeignKey(direction => direction.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(direction => direction.Property)
            .WithMany()
            .HasForeignKey(direction => new { direction.PropertyId, direction.OrganizationId })
            .HasPrincipalKey(property => new { property.Id, property.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
