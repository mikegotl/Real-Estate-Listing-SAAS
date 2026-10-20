using ListingStudio.Domain.Administration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ListingStudio.Infrastructure.Persistence.Configurations;

public sealed class PlatformMetricsResetConfiguration : IEntityTypeConfiguration<PlatformMetricsReset>
{
    public void Configure(EntityTypeBuilder<PlatformMetricsReset> builder)
    {
        builder.ToTable("PlatformMetricsResets");
        builder.HasKey(item => item.Id);
        builder.HasIndex(item => item.ResetAtUtc);
    }
}
