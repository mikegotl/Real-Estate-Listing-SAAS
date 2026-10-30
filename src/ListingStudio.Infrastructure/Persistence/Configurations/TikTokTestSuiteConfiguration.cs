using ListingStudio.Domain.Campaigns;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ListingStudio.Infrastructure.Persistence.Configurations;

public sealed class TikTokTestSuiteConfiguration : IEntityTypeConfiguration<TikTokTestSuite>
{
    public void Configure(EntityTypeBuilder<TikTokTestSuite> builder)
    {
        builder.ToTable("TikTokTestSuites");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.RequestedByUserId).HasMaxLength(450).IsRequired();
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(s => s.Error).HasMaxLength(500);
        builder.Ignore(s => s.CurrentJobId);
        builder.HasIndex(s => new { s.Status, s.UpdatedAtUtc });
        builder.HasIndex(s => s.OrganizationId).IsUnique().HasFilter("\"Status\" IN ('Queued', 'Running')");
    }
}
