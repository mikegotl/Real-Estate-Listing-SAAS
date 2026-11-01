using ListingStudio.Domain.Administration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ListingStudio.Infrastructure.Persistence.Configurations;

public sealed class FeatureFlagConfiguration : IEntityTypeConfiguration<FeatureFlag>
{
    public void Configure(EntityTypeBuilder<FeatureFlag> builder)
    {
        builder.ToTable("FeatureFlags");
        builder.HasKey(flag => flag.Key);
        builder.Property(flag => flag.Key).HasMaxLength(100);
        builder.Property(flag => flag.UpdatedByUserId).HasMaxLength(450);
        builder.HasMany(flag => flag.Organizations)
            .WithOne()
            .HasForeignKey(item => item.FeatureFlagKey)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class FeatureFlagOrganizationConfiguration : IEntityTypeConfiguration<FeatureFlagOrganization>
{
    public void Configure(EntityTypeBuilder<FeatureFlagOrganization> builder)
    {
        builder.ToTable("FeatureFlagOrganizations");
        builder.HasKey(item => new { item.FeatureFlagKey, item.OrganizationId });
        builder.Property(item => item.FeatureFlagKey).HasMaxLength(100);
        builder.HasIndex(item => item.OrganizationId);
        builder.HasOne<ListingStudio.Domain.Organizations.Organization>()
            .WithMany()
            .HasForeignKey(item => item.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
